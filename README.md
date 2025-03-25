# ALERT ALERT ALERT THE RELEASE IS BROKEN
For the current release to work, you must edit the two y-cruncher config files to fit the number of cores in your system.

Go to the y-cruncher folder in your download directory and open test_bkt_forever.cfg and test_bbp_forever.cfg.

Edit `LogicalCores : ["0-31"]` to `LogicalCores : ["0-15"]`, or `23`, or `11`, or even `7`, depending on how many cores you have, or if you have SMT on or off.

# Note about compatiblity with 4/6/12-core skus
I just realized that this won't work with these skus. ZenStates-Core does not implicitly compensate for the disabled cores when applying per-core PBO offsets. I need to update this to get the disabled core map and compensate for it myself.

# Ryzen PBO VID Synchronizer
An easy to use, no-frills tool for quickly synchronizing your VID in a multithreaded workload via PBO offsets on AMD Ryzen CPUs. If you are a normal person, with conventional, ambient cooling, this tool will get you >90% of the way towards your maximum possible, stable performance, in under an hour, hands-free.

## Why?
See this blog post for the full rationale behind this tool: [To be filled]

The TL;DR is this:
- Each CCD runs at a single clock speed
- The worst active core on a CCD determines the clock speed of the CCD
- The CPU package receives one voltage level for the core domain
- Desktop Ryzen CPUs do not have internal per-core voltage regulation enabled
- Bad cores can take more offset than good cores
- Some good cores can take almost zero offset before crashing in 1T workloads
- Telling people to put all cores to -30, -20, -15, even -10, is BAD, and you should STOP GIVING PEOPLE ADVICE THAT MAKES THEIR COMPUTERS CRASH.

This tool will gradually bring all cores to a relatively level playing field in multi-threaded workloads without risking your existing stability in lightly/1T workloads. You can continue to optimize from the settings produced by this tool using Curve Shaper on Zen 5, or by continuing to gently nudge the offsets downwards on platforms where Curve Shaper is unavailable. A tool like [CoreCycler](https://github.com/sp00n/corecycler) can help with this.

## Requirements:
- .NET Runtime 8.0 - https://dotnet.microsoft.com/en-us/download/dotnet/8.0
- HWiNFO64 - https://www.hwinfo.com/
- AMD Ryzen CPU supported by ZenStates-Core - https://github.com/irusanov/ZenStates-Core

## Installation
1. Download the above prerequisites, and make sure your CPU is supported.
2. Download the latest release from the sidebar.
3. Unzip on a local drive.

## Usage
[Screenshots to come later, but you should be able to solve this.]
1. Enable shared memory and snapshot CPU polling in HWiNFO.
2. Change the polling period to 200ms in HWiNFO.
3. Run `ryzen-pbo-vid-sync.exe` as Administrator.
4. Wait until it finishes.
5. Reboot and apply results to BIOS, or use [SMUDebugTool](https://github.com/irusanov/SMUDebugTool/tree/master), or [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli) to re-implement them at a later time.

# Case Study

[Screenshots to come later]

I have a watercooled 9950X. PBO offsets resulting from this script:

```
Core 0: -7
Core 1: 0
Core 2: -2
Core 3: -12
Core 4: -10
Core 5: -16
Core 6: -16
Core 7: -20
Core 8: -29
Core 9: -27
Core 10: -30
Core 11: -31
Core 12: -31
Core 13: -32
Core 14: -34
Core 15: -34
```

These values roughly, though not exactly, align with the factory-fused core performance precedence from AMD.

These values result in the two CCDs conjoining within <20MHz of each other and hitting HTFMax limits in y-cruncher BKT at ~5.48ghzGHz, reporting a PPT of ~230w. Under y-cruncher BBP, it reaches thermal throttling conditions at a reported PPT of ~260w and ~4.7GHz across all cores.

For comparison, under "stock" conditions - stock meaning, board limits removed, PBO enabled, but no offsets applied - the BKT PPT is ~275w, with a clock speed of ~5.38GHz on CCD0, and ~5.2GHz on CCD1. For BBP, CCD0 is at ~4.58GHz, CCD1 at ~4.35GHz, and PPT at ~255w. Again, with BKT, it's hitting HTFMax, and with BBP, it's thermal throttling.

HTFMax makes me sad.
