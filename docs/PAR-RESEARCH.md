# PAR research notes

This is the reference behind the radar model, display and phraseology in PAR Scope. Values marked
*unverified* could not be confirmed from a primary source.

## Real systems modeled

| Preset | Service / role | Scan | Coverage | Range | Update | Display |
|---|---|---|---|---|---|---|
| AN/FPN-68 (L3Harris PAR-2020) | USN/USMC, replacing FPN-63 | AESA | 30° az, −1° to +7° el | 20 NM (15 rain) | < 1 s | color, tracks, data blocks |
| AN/FPN-63 | USN/USMC shore PAR (1978–) | two mechanical antennas | 20° × 8° | 10/20 NM | ~1 s *unverified* | analog AZ-EL indicator, bright 5-mile marks, DH marker, no tracker |
| AN/GPN-22 Hi-PAR | USAF fixed base | phased-array feed, electronic both axes | 20° × 8° | 15 NM | ~1 s | tracks six aircraft |
| AN/FPN-67 FBPAR | US Army fixed base | electronic | ±10° az, −1° to +6° el | 15 NM | < 1 s | track-while-scan |
| AN/MPN-25 (GCA-2000) | USMC/Army transportable | phased array | 20° | 20 NM | 1 s | history dots, data blocks |
| ICAO Annex 10 | baseline standard | – | 20° × 7° | ≥ 9 NM | ≥ 1 per s | – |
| AN/SPN-46(V)2 PALS | USN, shore-based training version of the carrier ACLS | Ka-band tracker: one 0.57° conically scanned pencil beam locks on to an aircraft, X-band beacon channel | 25° az (*Wikipedia*); el *assumed* −1° to +7° | 10 NM; lock-on between 3.5 and 8 NM | tracker (*assumed* 4 per s) | two consoles, two aircraft each; see below |

Sources: radartutorial.eu system cards (via the Internet Archive); L3Harris PAR-2020 sell sheet;
Navy NRTC 14089 ch. 2; DTIC ADA228798 (1989 DoD PAR assessment); ICAO Annex 10 Vol I §3.2.

### AN/SPN-46 PALS (October 2026)

* **Radar** (Sierra Nevada Corporation FCC experimental-licence filing for the AN/SPN-46(V)6, April 2022):
  33.2 GHz (33.0–33.4) magnetron, 50 kW peak, 0.2 µs pulse, 1,750–2,080 pps; parabolic reflector with a
  nutating Ka-band feed (conical scan, 100 Hz) and a 2-axis monopulse X-band receive feed (9.31 GHz);
  Ka-band beamwidth 0.57° (3 dB); antenna travel 30° left to 60° right and +30° to −15°.
* **Variants** (NAVEDTRA 14089 *Electronics Technician Vol. 4* ch. 2, 1993/1995): the (V)1 on carriers; the
  (V)2 "installed at selected naval air stations … for the PALS training and the PALS certification of
  aircraft", the same as the (V)1 without the Mk 16 stable elements and with a 7 ft antenna instead of 4 ft.
  Two identical operator control consoles (OCC), "one for each channel"; "The OCC includes a radar display,
  a data generator, and an embedded computer." Each console "can track (lock on) two aircraft at a time"
  (NAVEDTRA 14342A §11). DAIR "causes aircraft symbols and side numbers of DAIR-tracked aircraft to appear
  on the console display" (14342A), hence the callsign-only data block.
* **Range, coverage, precision**: 10 NM, 25° azimuth, 2 ft (Wikipedia infobox, unverified). Update rate and
  elevation coverage not found; assumed.
* **Modes** (NAVAIR 00-80T-105 *CV NATOPS*, July 2009, definitions and §6.4.7): I automatic to touchdown;
  IA automatic to 200 ft and 1/2 NM; II error needles in the cockpit; IID flight director; IIT Mode II flown
  with Mode III information; III "a pilot is supplied position guidance information from the AN/SPN-42 or
  AN/SPN-46 by a final controller". OPNAV M-3710.7 (Sept 2023) p. 228 logs Mode III as a PAR approach.
* **Procedure** (CV NATOPS §6.4.7.4–6.4.7.8, §6.4.13.2, Fig. 6-6): "The final controller will acquire the
  aircraft between 3.5 and 8 miles" and report "201 lock-on, 5 miles. Say needles."; the pilot reports needle
  position ("201, needles up and right"); "approaching tipover" (glidepath), range each mile; "at minimums"
  unless the LSO has taken over; "Lock-on must be maintained until touchdown"; after lock-on, soft headings
  ("301, right five"). Minimums for Modes IA, II, IIT and III: 200 ft and 1/2 NM. §6.4.13.2: "Standard ATC
  and precision instrument approach phraseology shall be used" except fuel states, angels and soft headings,
  so the AI's carrier wording changes only the headings, the lock-on report and "at minimums". Not done:
  "say needles" (the simulated pilots fly Mode III, no needles to report) and angels (the brief keeps feet).
* Not found: the SPN-46 technical manual and the Navy Training Plan (NTP E-50-8206C, April 1989, cited by
  NAVEDTRA 14089); nothing public describes the console screen.

## ICAO accuracy budget (Annex 10 Vol I §3.2)

* Azimuth: ≤ 0.6 % of antenna range + 10 % of deviation, or 30 ft. Resolution 1.2°.
* Elevation: ≤ 0.4 % of antenna range + 10 % of deviation, or 20 ft. Resolution 0.6°.
* Range: ≤ 100 ft + 3 % of distance from touchdown. Resolution 400 ft.
* Siting: about 3,000 ft past touchdown and 400 ft from the centerline when scanning ±10°.

The simulator uses one third of the azimuth and elevation budgets as 1σ noise, scaled per preset.
Range noise is 30 ft + 0.3 % of distance from touchdown (one third of the fixed term, one tenth of the
percentage term), well inside the budget; a third of 3 % would scatter returns by 300 ft at 5 NM.
Coverage, lock-on, target size and noise use antenna (slant) range; the display axis, calls, data
block and safe limits use distance from touchdown. It adds ground-reflection
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
  * full-height range lines, dashed green for each mile and solid pale green at 5 NM, labeled "1.00";
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

* **Close-up of an AN/TPN-31 ATNAVICS display** (Rugged Portable Systems monitor, Army PAR video frame,
  October 2026 audit), used for the TPN-31 preset:
  * compressed range axis labeled "0.00" … "9.00" in green under the elevation display;
  * green dashed line at each mile, solid green at 1 and 5 NM, cream dotted lines at each half mile and,
    in the first mile, each quarter mile;
  * cream solid coverage limits; on the elevation display about −0.5° and +6° measured against the 3°
    glidepath (straight-line log scaling keeps the ratio of their slopes);
  * pale green glidepath and course lines with no mile ticks, a cream dotted line under the glidepath;
  * a pale cyan decision-height line from just past touchdown to about a mile beyond DH, with a tick up at DH;
  * no ground line, touchdown boxes or runway outline;
  * a white filled target with a short dashed history trail behind it, and a leader line to a data block
    of the four-digit beacon code alone ("1235").
* **Earlier ATNAVICS footage** (Army radar training simulator, USMC expeditionary screens): the same
  compressed axis, green cursors and cream limits.

* **AN/SPN-46 console photos** (Navy photos of CATCC consoles, one labelled SPN-46A, and a 3D model of an
  OCC), used for the SPN-46 preset; nothing in the manuals describes the screen:
  * green line work on black; an elevation box over an azimuth box at the left, with a header above;
  * ten whole-mile lines from touchdown at the left edge, each gap about 0.63 of the one before (measured on the
    model: 1 NM at 37 % of the width, 2 NM at 61 %, 3 NM at 76 %, 4 NM at 85 %): x ∝ 1 − e^(−r/k), k = range
    scale / 4.6 (2.2 NM on the 10 NM scale);
  * height compressed the same way in glidepath terms (k · tan GPA, about 690 ft), so the glidepath runs straight
    from the bottom-left corner to the top-right; the model's height lines at 0.54, 0.79, 0.91 and 0.95 of the
    box fit lines every 500 ft; three straight glidepath lines that close up toward the far corner (the limits drawn
    from 6 % of the box either side of touchdown, as on the photos, not at a fixed angle, which this scale would bend);
  * azimuth: the same compression in feet off the course, lines every 500 ft to 2,000 ft either side (fits the
    model's lines; *assumed*), with a pair of limit lines close either side of the course (100 ft *assumed*);
  * at the right, a crosshair (Mode III) and a square with crossed needles (Mode II), as identified by the
    user; their scales (±0.5° glidepath, ±1° course) and readouts are *assumed*.

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
  * "Over landing threshold" as the aircraft passes the centerline reflector (§6-323).
* **Waveoff phraseology:** "(call sign) tower directs waveoff, climb and maintain 1600…" (§6-316).
* **Lost communications:** "…(five/fifteen) seconds on final approach…" (§6-320).

The manual defers PAR target interpretation and safety limits to NAVAIR 00-80T-114 §10.2.6.6.4–5,
which is not public. That is why the app uses the UK RA 3291 bands for "slightly" and "well".

## VATSIM Velocity and vPilot

See [VELOCITY.md](VELOCITY.md).


## vPAR (Michael Oxner, 2001–2005)

vPAR QuadRadar and Transponder were written for VATSIM because its 5-second position updates are too
slow for a PAR. The Transponder ran next to Flight Simulator (FSUIPC) and sent positions straight to
the controller's QuadRadar over TCP at 0.5 s intervals, outside VATSIM. Native Delphi; documentation
and downloads at files.flying-fox.de/forum/vatsim/vpar.

* The analog Final display copies the Canadian Forces **503 PAR** at 14 Wing Greenwood: logarithmic
  range spacing (wider close in), a bright spot at the last hit and no trail.
* Elevation field of view 7° (3.5° either side of the glidepath); a Low Altitude Warning line 0.5°
  below the glidepath by default, leveling off at the DH; azimuth course limit lines at ±1.25°.
* Approach setup: threshold lat/lon, touchdown zone elevation, final approach course (magnetic), GPA,
  ground point intercept displacement (~1,000 ft), radar control limit, DH, magnetic variation.
* Transmit timer: yellow after 5 s (final), red after 30 s (traffic pattern) — the lost-communications
  intervals. vibe PAR's transmit timer uses the same 5 s / 30 s split.
* CF GCA phraseology (PARPhraseology.html): lost-communications instructions before the approach;
  "now on final approach, do not acknowledge further transmissions unless requested by radar"; course
  trends "correcting slowly/nicely/rapidly", "paralleling", "drifting"; "intercepting glidepath,
  commence descent … NOW"; "adjust rate of descent"; "dangerously below the glidepath, level off your
  aircraft, acknowledge"; "resume normal rate of descent"; tower clearance relayed with wind and "check
  gear down, acknowledge" at 2 miles; "passing through radar control limits". The pilot model
  understands these, and answers an explicit "acknowledge" even after "do not acknowledge".

## Published PAR data (FAA d-TPP Radar Minimums)

The FAA's digital terminal procedures include the DoD "Radar Instrument Approach Minimums" pages for US
military and joint-use airports (`aeronav.faa.gov/d-tpp/{cycle}/`, files `*RAD.PDF`, listed in the
d-TPP metafile). Each PAR row gives `GP/TCH/RPI` (glidepath angle, threshold crossing height, runway
point of intercept from the threshold) and `DA/HAT`. `src/PARScope.Core/Par/par-minimums.csv` holds
102 runways at 38 airports from cycle 2610. Many pages draw their tables as vector shapes, not text, so
the values were read from the rendered pages and checked: DA − HAT equals the NASR touchdown zone
elevation for every row, RPI agrees with TCH/tan(GP), and every row that does exist as text matched
digit for digit. Notable values: KNKX 24R/24L 3.2°; KNFL 31L/31R 3.5°; KNFG 21 3.6°; KPAM 2.5°.

The site setup uses the RPI as the glidepath intercept point (it allows for runway slope, unlike
TCH/tan GPA) and measures DH (HAT) from the touchdown zone elevation.

Lost communications intervals on these pages vary by facility: one minute in the pattern and 15 s on
final (Whiteman AFB), one minute and 5/15 s (MCAS Beaufort); 7110.65 §5-10-4 allows up to one minute in
the pattern and 5 or 15 s on final.

## Magnetic variation

US runways use NASR's variation of record (what procedures are built on). Elsewhere the World Magnetic
Model 2025 (NOAA/BGS, public domain coefficients) gives the declination at the threshold; the
implementation matches NOAA's published test values. ATC issues wind magnetic, so the METAR (true)
wind is converted when tower's clearance is relayed.

## ASR (surveillance) display assumptions

* The ASR site is the selected runway's threshold. Real ASR antennas sit elsewhere on the field and no
  public source lists their positions, so ASR ranges are measured from that threshold.
* Range is horizontal ground range, not slant range (about 0.04 NM short at 3,000 ft and 3 NM).
* Coverage 60 NM (ASR-9 class); range noise 60 m and azimuth 0.08°.
* Range rings every 5 NM around the site. The plan range setting (10/20/40 NM) is the distance from
  the site to the nearest screen edge; the site is drawn off center, so the view reaches about 1.45×
  that along the final and 0.55× behind the site.
* Mile ticks on the final course are measured from touchdown, like the PAR; whether real video maps
  measure from the threshold has not been checked.
* The AI final controller's ASR vectors use true positions, not the noisy ASR picture.

## Sources added in the October 2026 audit

* FAA JO 7110.65 (X text via SKYbrary mirror; BB is current): §5-10-11 "your missed approach procedure
  is…", §5-10-12 "after completing low approach/touch and go…", §5-10-14 conditional missed approach,
  §5-12-1/3/4 order (no elevation information before glidepath intercept), §5-12-7c "over landing
  threshold, (course)", §5-12-8a3 guidance ends at the threshold, §5-12-9 contact tower after landing,
  §2-1-25 wheels check dropped once the pilot has reported gear down.
* FAA Order 8260.3G (effective 1 July 2024) supersedes 8260.3F; the obstacle clearance surface
  references above are to F and their G paragraph numbers have not been re-checked.
* L3Harris PAR-2020/FPN-68A sell sheet: 30° azimuth, −1° to +7°, 20 NM, update under 1 s, CAT II.
* Leonardo PAR 2090C (Italian and Swiss air forces): X band, ±15° × 10°, 20 NM, ≥32 tracks.