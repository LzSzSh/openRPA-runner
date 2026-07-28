# OpenRPA runtime patches

`openrpa-utilities-force-bundled-browser.patch` records the OpenRPA 1.4.57.13
source customization used by Maxwell. Apply it after checking out the pinned
OpenRPA source revision before running the runtime staging build:

```powershell
git -C ..\upstream\openrpa apply ..\..\OpenRpaWorkflowLauncher\patches\openrpa-utilities-force-bundled-browser.patch
```

The patch routes browser launches from the `StartProcess` activity through
Maxwell's bundled Chromium when the host supplies the `MAXWELL_BUNDLED_CHROME`
environment variable.
