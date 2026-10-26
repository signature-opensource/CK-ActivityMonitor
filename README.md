# CK-ActivityMonitor

[![AppVeyor](https://ci.appveyor.com/api/projects/status/33mt75jgu2s5d2a0?svg=true)](https://ci.appveyor.com/project/Signature-OpenSource/ck-activitymonitor)
[![Licence](https://img.shields.io/github/license/signature-opensource/CK-ActivityMonitor.svg)](LICENSE)

This repository contains the `IActivityMonitor` definition, its primary implementation and the extension methods that send logs.
For the logging solution built on top of these packages, see [CK-Monitoring](https://github.com/signature-opensource/CK-Monitoring/blob/stable/README.md).

| Package | Description | Latest stable |
|---------|-------------|---------------|
| [CK.ActivityMonitor](CK.ActivityMonitor/README.md) | The monitor, its clients and the core logging infrastructure. | [![nuget](https://img.shields.io/nuget/v/CK.ActivityMonitor.svg?label=CK.ActivityMonitor)](https://www.nuget.org/packages/CK.ActivityMonitor/) |
| [CK.ActivityMonitor.SimpleSender](CK.ActivityMonitor.SimpleSender/README.md) | The extension methods that send log lines and groups. | [![nuget](https://img.shields.io/nuget/v/CK.ActivityMonitor.SimpleSender.svg?label=CK.ActivityMonitor.SimpleSender)](https://www.nuget.org/packages/CK.ActivityMonitor.SimpleSender/) |

Licensed under the MIT license - see [LICENSE](LICENSE).
