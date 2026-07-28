# Maxwell bundled browser extension

This directory is a Manifest V3 migration fork of OpenRPA's `addon2` browser
extension. `content.js` is the OpenRPA browser utility bundled as a static
content script, so the common selector-based browser activities do not rely on
runtime JavaScript injection.

Manifest V3 intentionally rejects the OpenRPA `ExecuteScript` activity when it
contains arbitrary workflow-provided JavaScript. Chrome prohibits that pattern.
The extension returns `MAXWELL_MV3_DYNAMIC_SCRIPT_UNSUPPORTED` instead of
reporting a false success. New reviewed browser capabilities must be added as
static code in this directory.
