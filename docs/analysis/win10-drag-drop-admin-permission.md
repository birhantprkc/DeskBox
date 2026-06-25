# Win10 Explorer Drag/Drop Permission Investigation

Date: 2026-06-26

## Summary

Some Windows 10 users report that files from Explorer cannot be dragged into DeskBox widgets. The cursor may show a blocked/drop-not-allowed state, and DeskBox may not receive normal drag/drop diagnostics.

The latest diagnostic log points to a permission isolation issue instead of a file-format parsing issue:

```text
Process integrity isAdminRole=True tokenElevated=True integrity=High(0x3000)
```

There are no matching `[DropDiagnostic]` lines during the failed drag attempt. That means the drag operation does not reach DeskBox's WinUI `DragOver` / `Drop` handlers.

## Current Conclusion

DeskBox is still running with a high-integrity administrator token. Windows Explorer normally runs at medium integrity. Windows blocks drag/drop from a medium-integrity process into a high-integrity process, so Explorer-to-DeskBox file drag/drop can fail before DeskBox sees any data.

This is not primarily a Win10 file format issue when `[DropDiagnostic]` is absent. If DeskBox receives drag events but cannot parse paths, logs should include lines such as:

```text
[DropDiagnostic] ... formats=...
[DropDiagnostic] GetStorageItemsAsync count=... pathCount=...
```

## User Guidance

If file drag/drop from Explorer into DeskBox fails, first check whether DeskBox is running as administrator.

DeskBox should not be launched with administrator privileges for normal use. If DeskBox is elevated, Windows may prevent regular Explorer windows from dragging files into it.

Things to check:

1. Right-click `DeskBox.exe` or the desktop/start-menu shortcut.
2. Open Properties.
3. Check Compatibility.
4. Make sure "Run this program as an administrator" is not enabled.
5. Close DeskBox completely and start it again.

## Engineering Follow-Up

- Keep `Process integrity isAdminRole=... tokenElevated=... integrity=...` logging until the migration path is proven stable.
- Continue cleaning AppCompat `RUNASADMIN` flags for both old Program Files installs and the current user install path.
- Verify installer post-install launch does not inherit elevation.
- Consider a Settings/About diagnostic hint when `tokenElevated=True`.
- If future logs show `[DropDiagnostic]` but `pathCount=0`, investigate Win10 Explorer legacy formats such as `FileNameW`, `FileName`, `Shell IDList Array`, and possible native OLE drop target fallback.

