# METAR Implementation Plan

## Overview
Implement METAR weather display with two sources (aviationweather.gov and VATSIM), replacing current NASR status location.

## Requirements
1. **Two METAR Sources:**
   - aviationweather.gov: `https://aviationweather.gov/api/data/metar?ids={ICAO}&format=json`
   - VATSIM: `https://metar.vatsim.net/{ICAO}`

2. **UI Changes:**
   - Move NASR status to top-right corner (same line, right-aligned)
   - Display METAR where NASR status currently is (left side status bar)
   - Add "Display → METAR Source" menu with NOAA/VATSIM options

3. **METAR Display:**
   - Default: Full METAR text
   - Click to toggle: Full ↔ Abbreviated
   - Abbreviated format: Time (ends in Z) + Wind (includes KT/MPS) + Altimeter (begins with A/Q)
   - Auto-refresh every 5 minutes

4. **METAR Loading:**
   - Load when airport/runway selected
   - Load on startup if runway cached
   - Use ICAO code from NASR data (this is the ONLY place we use ICAO)

## Implementation Steps
1. Add MetarSource enum (NOAA, VATSIM)
2. Add METAR menu items to Display menu
3. Modify MainWindow.xaml layout (move NASR, add METAR TextBlock)
4. Add METAR fetching methods for both sources
5. Add METAR parsing/abbreviation logic
6. Add 5-minute refresh timer
7. Wire up click-to-toggle functionality
8. Integrate with runway selection
9. Add settings persistence (source + show full flag)

## Files to Modify
- MainWindow.xaml (UI layout changes)
- MainWindow.xaml.cs (METAR logic, timers, event handlers)
- UserSettings.cs (persist METAR preferences)

## Status
Ready to implement.
