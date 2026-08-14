# OpenRPA runtime patches

`0001-Add-Maxwell-runtime-compatibility.patch` is the complete OpenRPA 1.4.57.13
customization frozen for the 2026-08-14 Maxwell handoff. Apply it after
checking out the pinned OpenRPA source revision before running the runtime
staging build:

```powershell
git -C ..\upstream\openrpa am ..\..\OpenRpaWorkflowLauncher\patches\0001-Add-Maxwell-runtime-compatibility.patch
```

The complete patch includes browser Native Messaging, bundled-browser routing,
Windows UI Automation selector, notification, clipboard, and archive-path
compatibility changes.

`openrpa-utilities-force-bundled-browser.patch` is retained only as the earlier
single-purpose historical patch. Do not apply it after the complete patch.
