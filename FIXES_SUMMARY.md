# NASR Cache Persistence & Window Position Fixes

## Issues Fixed

### 1. NASR Cache Not Persisting Across App Restarts ✓

**Problem**: NASR data had to be re-downloaded every time the app was launched, even though it was being saved to cache.

**Root Cause**: The `SaveAppState()` method (which embeds the NASR cache into `app_state.json`) was only called when the window closed. If the app crashed, was killed, or closed abnormally, the NASR cache wouldn't be embedded in `app_state.json`, so on next launch it wouldn't be restored.

**Solution**: Added immediate `SaveAppState()` calls after successful NASR data load:
- In `OnDownloadNASRClick()` - after downloading latest NASR data
- In `OnLoadNASRFileClick()` - after loading NASR from local file

This ensures the NASR cache is immediately persisted to `app_state.json` as soon as it's loaded, regardless of how the app is closed.

**Files Modified**:
- `PARScopeDisplay/MainWindow.xaml.cs` - Added `SaveAppState()` calls in download handlers (lines ~1989, ~2057)

---

### 2. Window Maximized on Secondary Monitor Restores to Primary Monitor ✓

**Problem**: When the window was maximized on a secondary monitor, closing and reopening the app would restore it maximized on the primary monitor instead.

**Root Cause**: WPF's `WindowState.Maximized` defaults to the primary monitor unless the window's position is set BEFORE maximizing. The window position was being restored, but maximizing happened immediately after without giving WPF time to anchor to the correct screen.

**Solution**:
1. **Extended `WindowPosition` class** to capture screen bounds:
   - Added `ScreenLeft`, `ScreenTop`, `ScreenWidth`, `ScreenHeight` properties
   - These are captured when saving window position using `System.Windows.Forms.Screen.FromHandle()`

2. **Updated save methods** to capture screen information:
   - `SaveWindowPosition()` - Captures screen bounds of current monitor
   - `SaveAppState()` - Captures screen bounds when embedding window position

3. **Fixed restore sequence** in both `LoadWindowPosition()` and `LoadAppState()`:
   - Set `Left`, `Top`, `Width`, `Height` first
   - For maximized windows:
     - Set `WindowState = Normal`
     - Call `UpdateLayout()` to force WPF to apply position
     - Set `WindowState = Maximized` to maximize on correct monitor

**Files Modified**:
- `PARScopeDisplay/MainWindow.xaml.cs`:
  - `WindowPosition` class (lines ~2500-2512)
  - `SaveWindowPosition()` (lines ~2091-2123)
  - `LoadWindowPosition()` (lines ~2064-2100)
  - `LoadAppState()` window restore section (lines ~2328-2358)
  - `SaveAppState()` window save section (lines ~2442-2458)
- `PARScopeDisplay/PARScopeDisplay.csproj`:
  - Added `System.Drawing` reference (required for `Screen.Bounds` which returns `Rectangle`)

---

## Technical Details

### Cache Persistence Flow

**Before Fix**:
1. User downloads NASR → `SaveCache()` writes `nasr_cache.json`
2. User closes app → `SaveAppState()` embeds `nasr_cache.json` into `app_state.json` (only if app closes normally!)
3. User reopens app → `LoadAppState()` restores `nasr_cache.json` from `app_state.json`
4. `NASRDataLoader` constructor loads from `nasr_cache.json`

**Problem**: If step 2 never happens (crash, task kill, etc.), the cache is never embedded in `app_state.json`.

**After Fix**:
1. User downloads NASR → `SaveCache()` writes `nasr_cache.json` **AND** `SaveAppState()` immediately embeds it
2. Cache is now persistent regardless of how app closes
3. On restart, `LoadAppState()` always finds the embedded cache and restores it

### Window Position Flow

**Before Fix**:
```csharp
this.Left = pos.Left;
this.Top = pos.Top;
this.Width = pos.Width;
this.Height = pos.Height;
if (pos.IsMaximized)
    this.WindowState = WindowState.Maximized; // Maximizes on primary monitor!
```

**After Fix**:
```csharp
this.Left = pos.Left;
this.Top = pos.Top;
this.Width = pos.Width;
this.Height = pos.Height;
if (pos.IsMaximized)
{
    this.WindowState = WindowState.Normal;  // Ensure not maximized
    this.UpdateLayout();                     // Force WPF to apply position
    this.WindowState = WindowState.Maximized; // Now maximizes on correct monitor
}
```

The `UpdateLayout()` call ensures WPF processes the position change before maximizing, anchoring the window to the correct monitor.

---

## Build Status

✅ **Build Successful**
- 0 Errors
- 0 Warnings
- All projects compiled cleanly

---

## Testing Recommendations

### Test NASR Cache Persistence:
1. Launch app
2. Download NASR data (Data → Download Latest NASR)
3. Verify success message shows airport count
4. **Immediately** kill the app process (Task Manager → End Task) - don't close normally
5. Relaunch app
6. Check NASR status - should show "(cached)" with correct airport count
7. Open Runway Selection - should populate with airports from cache

### Test Window Position on Secondary Monitor:
1. If you have multiple monitors, move app to secondary monitor
2. Maximize the window (not full screen, just Windows maximize)
3. Close app normally
4. Reopen app
5. Verify window opens maximized on the **same monitor** where it was before

---

## Files Changed Summary

| File | Changes |
|------|---------|
| `PARScopeDisplay/MainWindow.xaml.cs` | • Added `SaveAppState()` calls after NASR download/load<br>• Extended `WindowPosition` class with screen bounds<br>• Updated `SaveWindowPosition()` to capture screen info<br>• Fixed `LoadWindowPosition()` maximize sequence<br>• Fixed `LoadAppState()` window restore sequence<br>• Updated `SaveAppState()` to embed screen bounds |
| `PARScopeDisplay/PARScopeDisplay.csproj` | • Added `System.Drawing` assembly reference |

---

## Cache File Locations

All cache files are stored in: `%APPDATA%\VATSIM-PAR-Scope\`

- `nasr_cache.json` - Direct NASR cache file (written by `NASRDataLoader.SaveCache()`)
- `app_state.json` - Consolidated app state including embedded NASR cache, window position, UI settings
- `window_position.json` - Legacy window position file (kept for fallback compatibility)

The NASR cache is now stored in **both** locations for redundancy:
1. `nasr_cache.json` - Direct cache file
2. Embedded in `app_state.json` - Backup copy that's immediately written after download

---

## Version Control

All changes committed to git. Use `git diff` to review specific code changes.

**Commit message suggestion**:
```
Fix NASR cache persistence and multi-monitor window position

- Add immediate SaveAppState() calls after NASR download/load
  to ensure cache persists regardless of how app closes
- Capture screen bounds in WindowPosition for multi-monitor support
- Fix window maximize sequence: set position, force layout, then maximize
  to ensure window restores to correct monitor
- Add System.Drawing reference for Screen.Bounds API

Fixes: NASR requiring re-download on every launch
Fixes: Maximized window on secondary monitor restoring to primary
```
