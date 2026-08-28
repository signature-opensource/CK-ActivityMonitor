# CK-ActivityMonitor

[![AppVeyor](https://ci.appveyor.com/api/projects/status/33mt75jgu2s5d2a0?svg=true)](https://ci.appveyor.com/project/Signature-OpenSource/ck-activitymonitor)
[![Licence](https://img.shields.io/github/license/signature-opensource/CK-ActivityMonitor.svg)](LICENSE)

This repository contains `IActivityMonitor` definition and its primary implementation along with helpers.  
See [CK-Monitoring](https://github.com/Invenietis/CK-Monitoring) for integration and use as a logging solution in .Net projects.

| Package | Description | Latest stable |
|---------|-------------|---------------|
| [CK.ActivityMonitor](CK.ActivityMonitor/README.md) | The monitor, its clients, the design rationale, `AsyncLock`, `StaticGate`, the static logger and tag filtering. | [![nuget](https://img.shields.io/nuget/v/CK.ActivityMonitor.svg?label=CK.ActivityMonitor)](https://www.nuget.org/packages/CK.ActivityMonitor/) |
| [CK.ActivityMonitor.SimpleSender](CK.ActivityMonitor.SimpleSender/README.md) | The extension methods you actually call: `Debug`/`Trace`/`Info`/`Warn`/`Error`/`Fatal`, `OpenXxx` groups, and the interpolated handlers that skip message evaluation when a log is filtered out. | [![nuget](https://img.shields.io/nuget/v/CK.ActivityMonitor.SimpleSender.svg?label=CK.ActivityMonitor.SimpleSender)](https://www.nuget.org/packages/CK.ActivityMonitor.SimpleSender/) |

Start with [CK.ActivityMonitor.SimpleSender](CK.ActivityMonitor.SimpleSender/README.md) to send logs,
and read [CK.ActivityMonitor](CK.ActivityMonitor/README.md) for why the monitor is a parameter rather
than an ambient context, and for what this logger is trying to be.

Licensed under the MIT licence - see [LICENSE](LICENSE).
