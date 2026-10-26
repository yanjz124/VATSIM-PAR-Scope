# PAR research notes

This is the reference behind the radar model, display and phraseology in PAR Scope. Values marked
*unverified* could not be confirmed from a primary source.

## Real systems modelled

| Preset | Service / role | Scan | Coverage | Range | Update | Display |
|---|---|---|---|---|---|---|
| AN/FPN-68 (L3Harris PAR-2020) | USN/USMC, replacing FPN-63 | AESA | 30° az, −1° to +7° el | 20 NM (15 rain) | < 1 s | colour, tracks, data blocks |
| AN/FPN-63 | USN/USMC shore PAR (1978–) | two mechanical antennas | 20° × 8° | 10/20 NM | ~1 s *unverified* | analog AZ-EL indicator, bright 5-mile marks, DH marker, no tracker |
| AN/GPN-22 Hi-PAR | USAF fixed base | phased-array feed, electronic both axes | 20° × 8° | 15 NM | ~1 s | tracks six aircraft |
| AN/FPN-67 FBPAR | US Army fixed base | electronic | ±10° az, −1° to +6° el | 15 NM | < 1 s | track-while-scan |
| AN/MPN-25 (GCA-2000) | USMC/Army transportable | phased array | 20° | 20 NM | 1 s | history dots, data blocks |
| ICAO Annex 10 | baseline standard | – | 20° × 7° | ≥ 9 NM | ≥ 1 per s | – |

Sources: radartutorial.eu system cards (via the Internet Archive); L3Harris PAR-2020 sell sheet;
Navy NRTC 14089 ch. 2; DTIC ADA228798 (1989 DoD PAR assessment); ICAO Annex 10 Vol I §3.2.

## ICAO accuracy budget (Annex 10 Vol I §3.2)

* Azimuth: ≤ 0.6 % of antenna range + 10 % of deviation, or 30 ft. Resolution 1.2°.
* Elevation: ≤ 0.4 % of antenna range + 10 % of deviation, or 20 ft. Resolution 0.6°.
* Range: ≤ 100 ft + 3 % of distance from touchdown. Resolution 400 ft.
* Siting: about 3,000 ft past touchdown and 400 ft from the centreline when scanning ±10°.

The simulator uses one third of this budget as 1σ noise, scaled per preset. It adds ground-reflection
multipath on elevation within two beamwidths of the horizon, which is the reason PAR elevation sectors
start at −1°.

## Display conventions

* Elevation on top, azimuth below, sharing a horizontal range axis with touchdown near the left edge
  (CPN-4, FPN-63, AIM 4-5).
* Range marks every mile, with brighter 5-mile marks (FPN-63).
* Elevation: the glidepath cursor; a lower safe-limit cursor at the glidepath angle less 0.5° (US) or
  derived from the obstacle clearance surface (UK MAA RA 3291); a decision-height marker
  ("PAR minimums marker", FPN-63).
* Azimuth: a 50 ft wide course line, with blue 2° and 5° lines diverging from touchdown (RA 3291).
* Modern digital displays plot in Cartesian coordinates about touchdown so the glidepath is a straight
  line (patent US6600442). Classic beta-scan displays plot angle against range, which curves the cursors.
* Symbology (patent US5907568, RA 3291): green raw video, a white centroid for correlated tracks,
  history dots, data blocks, and alerts in inverse video. A track must be seen in both azimuth and
  elevation before control is taken.
* Range scales of 20, 10 and 5 NM. RA 3291 says the 1 and 3 NM scales should not be used.

## Display reference imagery

The digital style is copied from two sources:

* **Frames of a current USMC expeditionary PAR screen** (DVIDS video 780898, MCAS Iwakuni, 2021):
  * black background;
  * full-height range lines, dashed green for each mile and solid pale green at 5 NM, labelled "1.00";
  * olive glidepath and course lines;
  * tan dashed coverage limits fanning from the antenna, asymmetric because of the antenna offset;
  * a white dotted line under the glidepath;
  * white plots, leader lines and labels;
  * a white-bordered status window with white Courier text.

  It shows no sweep, no glow, no PPI and no frames between the scopes. Its range axis is
  logarithmic, x ∝ ln(1 + r/k), while constant-angle lines stay straight. The "Log" option reproduces
  this by scaling heights by X(r)/r.
* **ITT GCA-2000 patent drawings** (US5907568 fig. 4A):
  * vertical axes in feet;
  * a perpendicular tick every mile on the glidepath and course line;
  * boxed touchdown markers;
  * a data block of callsign / speed and range / offsets in feet with trend arrows.

The analog style follows museum and archive photos of FPN-63-era indicators. They show green or
amber phosphor, compressed range marks, dashed sector edges, smeared raw video with afterglow, and
no alphanumerics at all.

## US military deviation terms and safety limits

The app uses these by default. Sources: NAVEDTRA 14342A (Nov 2011) ch. 10, NAVAIR 00-80T-114
(Jul 2009) ¶7.2.6.7, AFMAN 13-204v3 (Jul 2020) §7, and FM 3-04.303 §3.

**Deviation terms** come from Table 10-1 of NAVEDTRA 14342A and ¶7.2.6.7.4 of 00-80T-114. The
controller sets the smallest usable target and mentally divides the elevation return into quarters:

| Cursor position on the target | Call |
|---|---|
| Bisects it | on glidepath |
| Cuts a middle quarter | slightly above/below |
| Cuts an outer quarter | above/below |
| Doesn't touch it | well above/below |

The return is about as tall as the beam at that range, so these bands widen with distance. In the
app, the target half-size is 0.4 × antenna range × tan(beamwidth), and analog presets draw raw video
at that size. The same quarter logic is applied to the azimuth return for course calls.

**Safety limits** (¶7.2.6.7.5). Before DH, a missed approach is required when safety limits are
exceeded or there are radical target deviations:

* "TOO LOW FOR SAFE APPROACH": after final descent, the target is well below the glidepath and
  touching the lower safe limit cursor.
* At one mile, a target not touching the glidepath or course cursor gives "TOO HIGH/LOW/FAR
  RIGHT/LEFT".
* A radical descent gives "LOW ALTITUDE ALERT. CHECK YOUR ALTITUDE IMMEDIATELY".

**Lower safe limit ("B") cursor**: the glidepath angle less 0.5°, from the runway point of intercept
(00-80T-114 ¶7.2.6.7.3; AFMAN 13-204v3 ¶7.4.1.1).

**Lateral limits** (AFMAN 13-204v3 Table 7.1): 100 ft at ½ NM, then 200 ft per mile up to 1,200 ft
at 6 NM.

**Calls before final descent**:

* "APPROACHING GLIDEPATH" 10–30 s before descent (7110.65 §5-12-1).
* "WHEELS SHOULD BE DOWN" before descent on final (§2-1-25, USA/USN).
* "BEGIN DESCENT".

7110.65BB now says "AT DECISION ALTITUDE" (§5-12-6).

**Other procedures**:

* One aircraft per final controller (AFMAN 13-204v3 ¶7.6.1).
* Lost communications after 5 s on PAR final.
* Pilots do not acknowledge after "DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS". If a missed approach
  is then issued for exceeded safety limits, obtain a specific acknowledgement.

The UK RA 3291 fixed-feet bands remain available as an option.

## Safety limits (FAA Order 8260.3F)

* PAR final uses the ILS final-segment criteria (§11-2-4).
* The obstacle clearance surface slopes at 102/GPA (34:1 for 3°). It starts max(200, 1154 − TCH/tan GPA)
  ft from the threshold, at threshold elevation (§10-2-2). This surface is drawn as the lower safe limit.
* The "W" surface half-width is 0.036·d + 392.8 ft (§10-2-3). It is drawn as the optional lateral limits.

## Phraseology (FAA JO 7110.65 §5-10, §5-12)

* "APPROACHING GLIDEPATH", then "BEGIN DESCENT"; "WHEELS SHOULD BE DOWN" (USA/USN).
* Transmissions roughly every 5 s: "HEADING (hdg), (SLIGHTLY/WELL) ABOVE/BELOW GLIDEPATH (AND COMING
  DOWN / HOLDING), (SLIGHTLY/WELL) LEFT/RIGHT OF COURSE (AND CORRECTING)".
* "(n) MILES FROM TOUCHDOWN" at least once each mile.
* Below decision height, calls are advisory only. Safety-limit missed approaches ("TOO LOW FOR SAFE
  APPROACH… EXECUTE MISSED APPROACH") apply only above DH.
* Lost communications: no transmission for 5 seconds on final.

US services do not publish numeric bands for "slightly" and "well". The app uses UK RA 3291:

* Glidepath: within 60 ft is "slightly", beyond 100 ft is "well".
* Course: inside the 2° line is "slightly", outside the 5° line is "well".

## USMC facility practice (MCAS Cherry Point ATCFacO P3722.1S, Aug 2020)

* **Equipment:** AN/FPN-63 on a turntable serving four runways.
  * 8° elevation × 20° azimuth.
  * 10 or 20 NM range select; with MTI selected, the choice is 10 or 15 NM.
  * The 5-mile range marks are brighter than the others.
  * A "PAR Minimum Marker" on the elevation scan marks DH where the glidepath reaches DH altitude
    (§7-205, §6-323b).
* **Accuracy:** course within 30 ft or 0.2° at the threshold; range within ±2 %.
* **Touchdown:** 668–1,051 ft from the approach end, which matches TCH / tan GPA.
* **Final calls:**
  * "Over approach lights" at ½ mile.
  * "Over landing threshold" as the aircraft passes the centreline reflector (§6-323).
* **Waveoff phraseology:** "(call sign) tower directs waveoff, climb and maintain 1600…" (§6-316).
* **Lost communications:** "…(five/fifteen) seconds on final approach…" (§6-320).

The manual defers PAR target interpretation and safety limits to NAVAIR 00-80T-114 §10.2.6.6.4–5,
which is not public. That is why the app uses the UK RA 3291 bands for "slightly" and "well".

## VATSIM Velocity and vPilot

See [VELOCITY.md](VELOCITY.md).
