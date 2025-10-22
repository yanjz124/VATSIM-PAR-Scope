using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace PARScopeDisplay
{
    /// <summary>
    /// Loads and parses FAA NASR (National Airspace System Resources) runway data
    /// </summary>
    public class NASRDataLoader
    {
    // airport-level magnetic variation and state map
    private Dictionary<string, AptBaseInfo> _aptBaseMag;
        private const string NASR_BASE_URL = "https://nfdc.faa.gov/webContent/28DaySub/extra/";
        private Dictionary<string, List<RunwayEndData>> _runwayData;
    public string LastLoadedSource { get; private set; }
    public DateTime? LastLoadedUtc { get; private set; }

        public class RunwayEndData
        {
            public string AirportId { get; set; }
            public string RunwayId { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double TrueHeading { get; set; }
            public double FieldElevationFt { get; set; }
            public double ThrCrossingHgtFt { get; set; }
            // Additional fields from NASR CSV that we want to retain
            public string RwyIdCsv { get; set; }
            public string ApchLgtSystemCode { get; set; }
            // Airport-level magnetic variation (degrees). If not present in APT_BASE.csv use 0
            public double MagneticVariationDeg { get; set; }
            // Raw hemisphere string from APT_BASE.csv (may be empty)
            public string MagneticHemisphere { get; set; }
            // ICAO ID from APT_BASE.csv
            public string IcaoId { get; set; }
        }

        // Small container for airport base fields we extract from APT_BASE.csv
        public class AptBaseInfo
        {
            public double Mag { get; set; }
            public string Hem { get; set; }
            public string IcaoId { get; set; }
        }

        public NASRDataLoader()
        {
            _runwayData = new Dictionary<string, List<RunwayEndData>>(StringComparer.OrdinalIgnoreCase);
            LastLoadedSource = null;
            // Try to load cached data from AppData
            try { LoadCache(); } catch { }
        }

        /// <summary>
        /// Read airport IDs directly from the cached nasr_cache.json file (best-effort).
        /// Returns an empty list if file missing or parse fails.
        /// </summary>
        public System.Collections.Generic.List<string> ReadCachedAirportIds()
        {
            try
            {
                string path = GetCachePath();
                if (!File.Exists(path)) return new System.Collections.Generic.List<string>();
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var obj = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (obj == null) return new System.Collections.Generic.List<string>();
                if (!obj.TryGetValue("runways", out var runObj) || !(runObj is Dictionary<string, object> runDict))
                    return new System.Collections.Generic.List<string>();
                var keys = runDict.Keys.ToList();
                return keys;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ReadCachedAirportIds failed: " + ex.Message);
                return new System.Collections.Generic.List<string>();
            }
        }

        /// <summary>
        /// Attempts to download and load the latest NASR data
        /// </summary>
        public bool TryLoadLatestData(out string errorMessage)
        {
            errorMessage = null;
            try
            {
                // Try today, then step back up to 28 days to find a valid cycle file.
                DateTime now = DateTime.UtcNow;
                string lastError = null;
                for (int i = 0; i <= 28; i++)
                {
                    DateTime d = now.AddDays(-i);
                    string zipFilename = ConstructZipFilename(d);
                    string url = NASR_BASE_URL + zipFilename;

                    bool notFound;
                    if (TryDownloadAndParse(url, out errorMessage, out notFound))
                    {
                        LastLoadedSource = url;
                        // record the cycle date used to construct this filename
                        LastLoadedUtc = d; // the date attempted in the loop
                        SaveCache(); // Save to cache after successful load
                        return true;
                    }

                    lastError = errorMessage;
                    // 404 means try previous day; other errors likely won't be fixed by trying older files.
                    if (!notFound)
                        break;
                }

                errorMessage = lastError ?? "Unable to locate a valid NASR APT CSV zip in the last 28 days.";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = "Error loading NASR data: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Loads NASR data from a local zip file
        /// </summary>
        public bool TryLoadFromFile(string zipPath, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                if (!File.Exists(zipPath))
                {
                    errorMessage = "File not found: " + zipPath;
                    return false;
                }

                bool ok = ParseZipFile(zipPath, out errorMessage);
                if (ok)
                {
                    LastLoadedSource = zipPath;
                    // attempt to extract date from filename like DD_MMM_YYYY_APT_CSV.zip
                    try
                    {
                        var fn = Path.GetFileName(zipPath);
                        var parts = fn.Split('_');
                        if (parts.Length >= 3)
                        {
                            string day = parts[0]; string mon = parts[1]; string year = parts[2];
                            string dateStr = day + " " + mon + " " + year;
                            if (DateTime.TryParseExact(dateStr, "dd MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                            {
                                LastLoadedUtc = dt;
                            }
                        }
                    }
                    catch { }
                    SaveCache(); // Save to cache after successful load
                }
                return ok;
            }
            catch (Exception ex)
            {
                errorMessage = "Error loading file: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Gets runway data for a specific airport and runway
        /// </summary>
        public RunwayEndData GetRunway(string airportId, string runwayId)
        {
            if (string.IsNullOrEmpty(airportId)) return null;

            string wantKey = airportId.ToUpperInvariant();
            List<RunwayEndData> runways = null;

            // Try exact key
            if (!_runwayData.TryGetValue(wantKey, out runways))
            {
                // Try common US variation with leading 'K' or without
                if (wantKey.Length == 3)
                {
                    var k = "K" + wantKey;
                    if (_runwayData.TryGetValue(k, out runways))
                    {
                        Debug.WriteLine($"NASR: Resolved airport {airportId} -> {k}");
                        wantKey = k;
                    }
                }
                else if (wantKey.StartsWith("K") && wantKey.Length == 4)
                {
                    var s = wantKey.Substring(1);
                    if (_runwayData.TryGetValue(s, out runways))
                    {
                        Debug.WriteLine($"NASR: Resolved airport {airportId} -> {s}");
                        wantKey = s;
                    }
                }
            }

            // If still not found, try suffix/contains matches among loaded keys
            if ((runways == null || runways.Count == 0) && _runwayData != null && _runwayData.Count > 0)
            {
                var match = _runwayData.Keys.FirstOrDefault(k => k.EndsWith(wantKey, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    match = _runwayData.Keys.FirstOrDefault(k => k.IndexOf(wantKey, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrEmpty(match))
                {
                    _runwayData.TryGetValue(match, out runways);
                    Debug.WriteLine($"NASR: Resolved airport {airportId} -> {match} via suffix/contains");
                    wantKey = match;
                }
            }

            if (runways == null || runways.Count == 0) return null;

            if (string.IsNullOrEmpty(runwayId))
                return runways.FirstOrDefault();

            string want = NormalizeRunwayId(runwayId);
            var found = runways.FirstOrDefault(r => NormalizeRunwayId(r.RunwayId).Equals(want, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var folder = System.IO.Path.Combine(appData, "VATSIM-PAR-Scope");
                    System.IO.Directory.CreateDirectory(folder);
                    var logPath = System.IO.Path.Combine(folder, "startup_log.txt");
                    var avail = string.Join(",", runways.Select(r => r.RunwayId ?? "(null)"));
                    var msg = $"NASR: Runway {runwayId} not found for airport {airportId} (resolved key {wantKey}). Available: {avail}";
                    System.IO.File.AppendAllText(logPath, DateTime.UtcNow.ToString("o") + " " + msg + System.Environment.NewLine);
                    Debug.WriteLine(msg);
                }
                catch { }
            }
            return found;
        }

        /// <summary>
        /// Gets all runways for a specific airport
        /// </summary>
        public List<RunwayEndData> GetAirportRunways(string airportId)
        {
            if (string.IsNullOrEmpty(airportId)) return new List<RunwayEndData>();
            
            List<RunwayEndData> runways;
            if (_runwayData.TryGetValue(airportId.ToUpperInvariant(), out runways))
                return new List<RunwayEndData>(runways);
            
            return new List<RunwayEndData>();
        }

        /// <summary>
        /// Gets list of all airport IDs in the loaded data
        /// </summary>
        public List<string> GetAirportIds()
        {
            // Return only all-letter ICAO-like identifiers to keep dropdown small
            var ids = _runwayData.Keys.Where(k => !string.IsNullOrEmpty(k) && k.All(ch => char.IsLetter(ch))).ToList();
            // If in-memory map is empty (some machines may fail to populate in memory), try a best-effort read from the on-disk cache
            if ((ids == null || ids.Count == 0))
            {
                try
                {
                    var cacheIds = ReadCachedAirportIds();
                    if (cacheIds != null && cacheIds.Count > 0)
                    {
                        ids = cacheIds.Where(k => !string.IsNullOrEmpty(k) && k.All(ch => char.IsLetter(ch))).Select(k => k.ToUpperInvariant()).ToList();
                    }
                }
                catch { }
            }
            return ids;
        }

        private string ConstructZipFilename(DateTime date)
        {
            // Format: DD_MMM_YYYY_APT_CSV.zip (e.g., 02_Oct_2025_APT_CSV.zip)
            string day = date.Day.ToString("D2");
            string month = date.ToString("MMM", CultureInfo.InvariantCulture);
            string year = date.Year.ToString();
            return string.Format("{0}_{1}_{2}_APT_CSV.zip", day, month, year);
        }

        private bool TryDownloadAndParse(string url, out string errorMessage, out bool notFound)
        {
            errorMessage = null;
            string tempZip = null;
            notFound = false;
            
            try
            {
                // Download to temp file
                tempZip = Path.GetTempFileName();
                
                // Ensure TLS 1.2 for FAA site
                try
                {
#pragma warning disable SYSLIB0014
                    System.Net.ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
#pragma warning restore SYSLIB0014
                }
                catch { }

                using (WebClient client = new WebClient())
                {
                    client.DownloadFile(url, tempZip);
                }

                // Parse the downloaded file
                bool result = ParseZipFile(tempZip, out errorMessage);
                
                return result;
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null && resp.StatusCode == HttpStatusCode.NotFound)
                {
                    notFound = true;
                    errorMessage = "NASR file not found (404): " + url;
                }
                else
                {
                    errorMessage = "Failed to download NASR data from " + url + ": " + wex.Message;
                }
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = "Error processing NASR data: " + ex.Message;
                return false;
            }
            finally
            {
                // Clean up temp file
                if (tempZip != null && File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { }
                }
            }
        }

        private bool ParseZipFile(string zipPath, out string errorMessage)
        {
            errorMessage = null;
            
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    // Try to parse airport base file first to collect MAG_VARN/MAG_HEMIS per ARPT_ID
                    var aptBaseEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("APT_BASE.csv", StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, AptBaseInfo> aptMag = null;
                    if (aptBaseEntry != null)
                    {
                        using (var basestream = aptBaseEntry.Open())
                        using (var basereader = new StreamReader(basestream))
                        {
                            aptMag = ParseAptBaseCsv(basereader);
                        }
                    }
                    // Find APT_RWY_END.csv
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => 
                        e.Name.Equals("APT_RWY_END.csv", StringComparison.OrdinalIgnoreCase));

                    if (entry == null)
                    {
                        errorMessage = "APT_RWY_END.csv not found in archive";
                        return false;
                    }

                    // Parse CSV (pass aptMag via a field so ParseRunwayEndCsv can pick it up)
                    _aptBaseMag = new Dictionary<string, AptBaseInfo>(StringComparer.OrdinalIgnoreCase);
                    if (aptMag != null)
                    {
                        foreach (var kv in aptMag)
                        {
                            // copy in the parsed AptBaseInfo (includes ICAO ID if present)
                            _aptBaseMag[kv.Key] = new AptBaseInfo { Mag = kv.Value.Mag, Hem = kv.Value.Hem, IcaoId = kv.Value.IcaoId };
                        }
                    }
                    using (Stream stream = entry.Open())
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        var ok = ParseRunwayEndCsv(reader, out errorMessage);
                        if (ok)
                        {
                            try { WriteAptBaseAndRunwaysCsv(); } catch { }
                        }
                        return ok;
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = "Error reading zip file: " + ex.Message;
                return false;
            }
        }

        // Dumps two CSV files into AppData for external FAA-to-ICAO work:
        // - APT_BASE_extracted.csv : ARPT_ID, MAG_VARN, MAG_HEMIS, STATE_CODE
        // - NASR_Runways.csv : AirportId,RunwayId,Latitude,Longitude,TrueHeading,FieldElevFt,ThrCrossingHgtFt,RwyIdCsv,ApchLgtSystemCode,MagVar,MagHem,State
        private void WriteAptBaseAndRunwaysCsv()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "VATSIM-PAR-Scope");
            Directory.CreateDirectory(folder);
            var aptPath = Path.Combine(folder, "APT_BASE_extracted.csv");
            var runPath = Path.Combine(folder, "NASR_Runways.csv");

            using (var w = new StreamWriter(aptPath, false, Encoding.UTF8))
            {
                w.WriteLine("ARPT_ID,MAG_VARN,MAG_HEMIS,ICAO_ID");
                if (_aptBaseMag != null)
                {
                    foreach (var kv in _aptBaseMag.OrderBy(k => k.Key))
                    {
                        var a = kv.Key;
                        var info = kv.Value;
                        w.WriteLine($"{EscapeCsv(a)},{info.Mag.ToString(CultureInfo.InvariantCulture)},{EscapeCsv(info.Hem)},{EscapeCsv(info.IcaoId)}");
                    }
                }
            }

            using (var w = new StreamWriter(runPath, false, Encoding.UTF8))
            {
                w.WriteLine("AirportId,RunwayId,Latitude,Longitude,TrueHeading,FieldElevationFt,ThrCrossingHgtFt,RwyIdCsv,ApchLgtSystemCode,MagVar,MagHem,IcaoId");
                if (_runwayData != null)
                {
                    foreach (var kv in _runwayData.OrderBy(k => k.Key))
                    {
                        foreach (var r in kv.Value)
                        {
                            w.WriteLine($"{EscapeCsv(r.AirportId)},{EscapeCsv(r.RunwayId)},{r.Latitude.ToString(CultureInfo.InvariantCulture)},{r.Longitude.ToString(CultureInfo.InvariantCulture)},{r.TrueHeading.ToString(CultureInfo.InvariantCulture)},{r.FieldElevationFt.ToString(CultureInfo.InvariantCulture)},{r.ThrCrossingHgtFt.ToString(CultureInfo.InvariantCulture)},{EscapeCsv(r.RwyIdCsv)},{EscapeCsv(r.ApchLgtSystemCode)},{r.MagneticVariationDeg.ToString(CultureInfo.InvariantCulture)},{EscapeCsv(r.MagneticHemisphere)},{EscapeCsv(r.IcaoId)}");
                        }
                    }
                }
            }
        }

        private static string EscapeCsv(string s)
        {
            if (s == null) return "";
            if (s.IndexOfAny(new char[]{',','"','\n','\r'}) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private bool ParseRunwayEndCsv(StreamReader reader, out string errorMessage)
        {
            errorMessage = null;
            _runwayData.Clear();
            
            try
            {
                // Read header line
                string header = reader.ReadLine();
                if (header == null)
                {
                    errorMessage = "Empty CSV file";
                    return false;
                }

                // Parse header to find column indices (handle quoted fields)
                string[] headerCols = SplitCsvLine(header);
                // Normalized copy for matching
                Func<string, string> norm = s => (s ?? string.Empty).Trim().Trim('"').Replace(" ", "_").ToUpperInvariant();
                string[] headerNorm = headerCols.Select(norm).ToArray();
                
                    // Try multiple possible column names (favor RWY_END-specific fields)
                    int colAirportId = Array.FindIndex(headerNorm, c =>
                        c == "ARPT_ID" || c == "ARPT_IDENT" || c == "AIRPORT_ID" || c == "ARPT");

                    // Prefer RWY_END_ID strictly; only fall back to RWY_ID (pair) if necessary
                    int colRunwayEndId = Array.FindIndex(headerNorm, c =>
                        c == "RWY_END_ID" || c == "RWY_END_IDENT");

                    int colRunwayPairId = Array.FindIndex(headerNorm, c =>
                        c == "RWY_ID" || c == "RUNWAY_ID");

                    int colLat = Array.FindIndex(headerNorm, c =>
                        (c.Contains("RWY_END") && c.Contains("LAT") && (c.Contains("DEC") || c.EndsWith("_DD"))) ||
                        c == "RWY_END_LAT_DECIMAL" ||
                        c == "LAT_DECIMAL");

                    int colLon = Array.FindIndex(headerNorm, c =>
                        (c.Contains("RWY_END") && (c.Contains("LONG") || c.Contains("LON")) && (c.Contains("DEC") || c.EndsWith("_DD"))) ||
                        c == "RWY_END_LONG_DECIMAL" || c == "RWY_END_LON_DECIMAL" ||
                        c == "LONG_DECIMAL" || c == "LON_DECIMAL");

                    int colTrueHdg = Array.FindIndex(headerNorm, c =>
                        c == "TRUE_ALIGNMENT" ||
                        (c.Contains("TRUE") && (c.Contains("ALIGN") || c.Contains("BEARING") || c.Contains("BRG") || c.Contains("HDG"))));

                    int colElev = Array.FindIndex(headerNorm, c =>
                        (c.Contains("RWY_END") && c.Contains("ELEV")) ||
                        c == "RWY_END_ELEV" || c == "FIELD_ELEV");

                    int colTch = Array.FindIndex(headerNorm, c =>
                        c == "THR_CROSSING_HGT" || c == "THRESHOLD_CROSSING_HEIGHT" || c.Contains("THR") && c.Contains("CROSS") && c.Contains("HGT"));

                    // Optional fields we want to retain: raw RWY_ID and approach lighting system code
                    int colApchLgt = Array.FindIndex(headerNorm, c =>
                        c == "APCH_LGT_SYSTEM_CODE" || (c.Contains("APCH") && c.Contains("LGT")));

                if (colAirportId < 0 || (colRunwayEndId < 0 && colRunwayPairId < 0) || colLat < 0 || colLon < 0 || colTrueHdg < 0)
                {
                        string colInfo = string.Format("Header: {0}\n\nColumns: Airport={1}, RunwayEnd={2}, RunwayPair={3}, Lat={4}, Lon={5}, Hdg={6}",
                            header, colAirportId, colRunwayEndId, colRunwayPairId, colLat, colLon, colTrueHdg);
                        errorMessage = "Required columns not found.\n\n" + colInfo;
                    return false;
                }

                // Read data lines
                string line;
                int lineCount = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineCount++;
                    string[] cols = SplitCsvLine(line);
                    
                    int rwyCol = colRunwayEndId >= 0 ? colRunwayEndId : colRunwayPairId;
                    // capture optional column indexes
                    int colApchLgtLocal = Array.FindIndex(headerNorm, c => c == "APCH_LGT_SYSTEM_CODE" || (c.Contains("APCH") && c.Contains("LGT")));
                    if (cols.Length <= Math.Max(Math.Max(colAirportId, rwyCol),
                                                Math.Max(colLat, Math.Max(colLon, colTrueHdg))))
                        continue;

                    try
                    {
                        string airportId = cols[colAirportId].Trim().Trim('"');
                        
                        // Filter out alphanumeric airport IDs (containing digits)
                        if (airportId.Any(char.IsDigit)) continue;
                        
                        // Determine runway end id
                        string runwayId = null;
                        if (colRunwayEndId >= 0 && colRunwayEndId < cols.Length)
                        {
                            runwayId = cols[colRunwayEndId].Trim().Trim('"');
                        }
                        else if (colRunwayPairId >= 0 && colRunwayPairId < cols.Length)
                        {
                            string pair = cols[colRunwayPairId].Trim().Trim('"'); // like "16/34"
                            // Heuristic: pick the first token
                            if (!string.IsNullOrEmpty(pair))
                            {
                                int slash = pair.IndexOf('/');
                                runwayId = slash >= 0 ? pair.Substring(0, slash) : pair;
                            }
                        }
                        // keep raw runway id text in the data model; matching uses NormalizeRunwayId later
                        
                        if (string.IsNullOrEmpty(airportId) || string.IsNullOrEmpty(runwayId))
                            continue;

                        double lat, lon, hdg;
                        if (!double.TryParse(cols[colLat].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
                            continue;
                        if (!double.TryParse(cols[colLon].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
                            continue;
                        if (!double.TryParse(cols[colTrueHdg].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out hdg))
                            continue;

                        double elev = 0;
                        if (colElev >= 0 && colElev < cols.Length)
                        {
                            double.TryParse(cols[colElev].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out elev);
                        }
                        double tch = 0;
                        if (colTch >= 0 && colTch < cols.Length)
                        {
                            double.TryParse(cols[colTch].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out tch);
                        }

                        string rawRwyIdCsv = null;
                        if (rwyCol >= 0 && rwyCol < cols.Length)
                        {
                            rawRwyIdCsv = cols[rwyCol].Trim().Trim('"');
                        }
                        string apchLgtCode = null;
                        if (colApchLgtLocal >= 0 && colApchLgtLocal < cols.Length)
                        {
                            apchLgtCode = cols[colApchLgtLocal].Trim().Trim('"');
                        }

                        var data = new RunwayEndData
                        {
                            AirportId = airportId,
                            RunwayId = runwayId,
                            Latitude = lat,
                            Longitude = lon,
                            TrueHeading = hdg,
                            FieldElevationFt = elev,
                            ThrCrossingHgtFt = tch
                            ,RwyIdCsv = rawRwyIdCsv
                            ,ApchLgtSystemCode = apchLgtCode
                            // Merge airport-level magnetic variation if available
                            ,MagneticVariationDeg = (_aptBaseMag != null && _aptBaseMag.TryGetValue(airportId, out var mv) ? mv.Mag : 0.0)
                            ,MagneticHemisphere = (_aptBaseMag != null && _aptBaseMag.TryGetValue(airportId, out var mh) ? mh.Hem : string.Empty)
                            ,IcaoId = (_aptBaseMag != null && _aptBaseMag.TryGetValue(airportId, out var ms) ? ms.IcaoId : string.Empty)
                        };

                        List<RunwayEndData> list;
                        if (!_runwayData.TryGetValue(airportId, out list))
                        {
                            list = new List<RunwayEndData>();
                            _runwayData[airportId] = list;
                        }
                        list.Add(data);
                    }
                    catch
                    {
                        // Skip malformed lines
                        continue;
                    }
                }

                if (_runwayData.Count == 0)
                {
                    errorMessage = "No runway data found in CSV";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "Error parsing CSV: " + ex.Message;
                return false;
            }
        }

        private static string[] SplitCsvLine(string line)
        {
            if (line == null) return new string[0];
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped quote
                        sb.Append('"');
                        i++; // skip next quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (ch == ',' && !inQuotes)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(ch);
                }
            }
            list.Add(sb.ToString());
            return list.ToArray();
        }

        /// <summary>
        /// Parse APT_BASE.csv stream and return mapping ARPT_ID -> (MAG_VARN degrees, MAG_HEMIS string)
        /// </summary>
        private Dictionary<string, AptBaseInfo> ParseAptBaseCsv(StreamReader reader)
        {
            var dict = new Dictionary<string, AptBaseInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string header = reader.ReadLine();
                if (string.IsNullOrEmpty(header)) return dict;
                Func<string, string> norm = s => (s ?? string.Empty).Trim().Trim('"').Replace(" ", "_").ToUpperInvariant();
                var cols = SplitCsvLine(header).Select(norm).ToArray();
                
                // Debug log to see what columns are available
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var folder = System.IO.Path.Combine(appData, "VATSIM-PAR-Scope");
                    var logPath = System.IO.Path.Combine(folder, "apt_base_columns.txt");
                    System.IO.File.WriteAllText(logPath, string.Join("\n", cols));
                }
                catch { }
                
                int colArpt = Array.FindIndex(cols, c => c == "ARPT_ID" || c == "ARPT_IDENT" || c == "APRT_ID" || c == "APT_ID");
                int colMag = Array.FindIndex(cols, c => c == "MAG_VARN" || c == "MAG_VARN".ToUpperInvariant());
                int colHem = Array.FindIndex(cols, c => c == "MAG_HEMIS" || c == "MAG_HEMIS".ToUpperInvariant() || c == "MAG_HEMISPHERE");
                int colIcao = Array.FindIndex(cols, c => c == "ICAO_ID" || c == "ICAO_IDENT" || c == "ICAO");

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var parts = SplitCsvLine(line);
                    if (parts == null || parts.Length == 0) continue;
                    if (colArpt < 0 || colArpt >= parts.Length) continue;
                    string arpt = parts[colArpt].Trim().Trim('"');
                    if (string.IsNullOrEmpty(arpt)) continue;
                    double mag = 0.0;
                    string hem = string.Empty;
                    string icaoId = string.Empty;
                    if (colMag >= 0 && colMag < parts.Length)
                    {
                        var s = parts[colMag].Trim().Trim('"');
                        if (!string.IsNullOrEmpty(s))
                        {
                            double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mag);
                        }
                    }
                    if (colHem >= 0 && colHem < parts.Length)
                    {
                        hem = parts[colHem].Trim().Trim('"');
                    }
                    if (colIcao >= 0 && colIcao < parts.Length)
                    {
                        icaoId = parts[colIcao].Trim().Trim('"');
                    }

                    // Normalize hemisphere into signed variation where WEST is positive and EAST is negative
                    // So: Magnetic = True + MagVariationDeg  (True + West = Magnetic; True - East = Magnetic)
                    double signedMag = 0.0;
                    if (!string.IsNullOrEmpty(hem))
                    {
                        var h0 = hem.Trim().ToUpperInvariant();
                        if (h0.StartsWith("W")) signedMag = Math.Abs(mag);
                        else if (h0.StartsWith("E")) signedMag = -Math.Abs(mag);
                        else signedMag = mag;
                    }
                    else
                    {
                        signedMag = mag;
                    }
                    dict[arpt] = new AptBaseInfo { Mag = signedMag, Hem = hem, IcaoId = icaoId };
                }
            }
            catch { }
            return dict;
        }

        private string GetCachePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "VATSIM-PAR-Scope");
            try { Directory.CreateDirectory(folder); } catch { }
            return Path.Combine(folder, "nasr_cache.json");
        }

        private void SaveCache()
        {
            string logPath = null;
            try
            {
                // Create log file for troubleshooting
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string folder = Path.Combine(appData, "VATSIM-PAR-Scope");
                logPath = Path.Combine(folder, "nasr_save_log.txt");
                
                void Log(string msg)
                {
                    try 
                    { 
                        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); 
                        System.Diagnostics.Debug.WriteLine(msg);
                    } 
                    catch { }
                }
                
                Log($"NASR SaveCache: Starting...");
                
                var ser = new JavaScriptSerializer();
                // Increase max JSON length to handle large NASR datasets (default is 2MB)
                ser.MaxJsonLength = int.MaxValue;
                Log($"NASR SaveCache: Serializer created, MaxJsonLength set to unlimited");
                
                var dump = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                dump["runways"] = _runwayData.ToDictionary(k => k.Key, v => v.Value);
                Log($"NASR SaveCache: Runways dictionary created ({_runwayData.Count} entries)");
                
                // include metadata
                dump["meta"] = new Dictionary<string, object> { { "source", LastLoadedSource }, { "utc", LastLoadedUtc?.ToString("o") } };
                // include apt base magnetic map if present
                if (_aptBaseMag != null && _aptBaseMag.Count > 0)
                {
                    var magmap = _aptBaseMag.ToDictionary(k => k.Key, v => new { mag = v.Value.Mag, hem = v.Value.Hem, icao = v.Value.IcaoId });
                    dump["aptMag"] = magmap;
                    Log($"NASR SaveCache: Magnetic variation data added ({_aptBaseMag.Count} entries)");
                }
                
                Log($"NASR SaveCache: Serializing to JSON...");
                string json = ser.Serialize(dump);
                Log($"NASR SaveCache: JSON serialized successfully ({json.Length} bytes)");
                
                string cachePath = GetCachePath();
                Log($"NASR SaveCache: Writing cache to: {cachePath}");
                File.WriteAllText(cachePath, json, Encoding.UTF8);
                Log($"NASR SaveCache: File.WriteAllText completed");
                
                // Verify file was written
                if (File.Exists(cachePath))
                {
                    var fileInfo = new FileInfo(cachePath);
                    Log($"NASR SaveCache: SUCCESS! Cache file verified ({fileInfo.Length} bytes)");
                }
                else
                {
                    Log($"NASR SaveCache: ERROR - Cache file was not created!");
                }
            }
            catch (Exception ex)
            {
                string msg = $"NASR SaveCache: EXCEPTION - {ex.GetType().Name}: {ex.Message}\r\nStack: {ex.StackTrace}";
                try { if (logPath != null) File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
                System.Diagnostics.Debug.WriteLine(msg);
            }
        }

        private void LoadCache()
        {
            string logPath = null;
            try
            {
                // Prepare load log (helps in Release builds without debugger)
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string folder = Path.Combine(appData, "VATSIM-PAR-Scope");
                logPath = Path.Combine(folder, "nasr_load_log.txt");
                void Log(string msg)
                {
                    try { File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
                    System.Diagnostics.Debug.WriteLine(msg);
                }

                string path = GetCachePath();
                if (!File.Exists(path))
                {
                    Log($"LoadCache: NASR cache not found at: {path}");
                    return;
                }
                string json = File.ReadAllText(path, Encoding.UTF8);
                Log($"LoadCache: Read cache from {path} ({json.Length} bytes)");
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var obj = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (obj == null) return;
                _runwayData.Clear();
                // Expect cached structure: { runways: { ARPT: [ ... ] }, aptMag: { ARPT: { mag: number, hem: string } } }
                object runwaysObj = null;
                Dictionary<string, object> runDict = null;
                // Primary: expect { runways: { ARPT: [ ... ] } }
                if (obj.TryGetValue("runways", out runwaysObj) && runwaysObj is Dictionary<string, object> rd1)
                {
                    runDict = rd1;
                }
                else
                {
                    // Fallback: sometimes the cached JSON may already be the runways dictionary at top-level
                    // Detect if most top-level values are arrays of runway objects and treat obj as the runDict
                    bool looksLikeRunDict = obj.Count > 0 && obj.Values.All(v => v is object[]);
                    if (looksLikeRunDict)
                    {
                        runDict = obj; // safe to treat top-level as runways map
                    }
                }

                if (runDict != null)
                {
                    foreach (var kv in runDict)
                    {
                        var list = new List<RunwayEndData>();
                        var arr = kv.Value as object[];
                        if (arr == null) continue;
                        foreach (var it in arr)
                        {
                            var map = it as Dictionary<string, object>;
                            if (map == null) continue;
                            var r = new RunwayEndData();
                            try { r.AirportId = map.ContainsKey("AirportId") ? (string)map["AirportId"] : kv.Key; } catch { r.AirportId = kv.Key; }
                            try { r.RunwayId = map.ContainsKey("RunwayId") ? (string)map["RunwayId"] : null; } catch { r.RunwayId = null; }
                            try { r.Latitude = map.ContainsKey("Latitude") ? Convert.ToDouble(map["Latitude"]) : 0; } catch { r.Latitude = 0; }
                            try { r.Longitude = map.ContainsKey("Longitude") ? Convert.ToDouble(map["Longitude"]) : 0; } catch { r.Longitude = 0; }
                            try { r.TrueHeading = map.ContainsKey("TrueHeading") ? Convert.ToDouble(map["TrueHeading"]) : 0; } catch { r.TrueHeading = 0; }
                            try { r.FieldElevationFt = map.ContainsKey("FieldElevationFt") ? Convert.ToDouble(map["FieldElevationFt"]) : 0; } catch { r.FieldElevationFt = 0; }
                            try { r.ThrCrossingHgtFt = map.ContainsKey("ThrCrossingHgtFt") ? Convert.ToDouble(map["ThrCrossingHgtFt"]) : 0; } catch { r.ThrCrossingHgtFt = 0; }
                            try { r.RwyIdCsv = map.ContainsKey("RwyIdCsv") ? (string)map["RwyIdCsv"] : null; } catch { r.RwyIdCsv = null; }
                            try { r.ApchLgtSystemCode = map.ContainsKey("ApchLgtSystemCode") ? (string)map["ApchLgtSystemCode"] : null; } catch { r.ApchLgtSystemCode = null; }
                            list.Add(r);
                        }
                        _runwayData[kv.Key] = list;
                    }
                }

                // Restore aptMag if present
                _aptBaseMag = new Dictionary<string, AptBaseInfo>(StringComparer.OrdinalIgnoreCase);
                if (obj.TryGetValue("aptMag", out var aptMagObj) && aptMagObj is Dictionary<string, object> aptMagDict)
                {
                    foreach (var kv in aptMagDict)
                    {
                        try
                        {
                            var inner = kv.Value as Dictionary<string, object>;
                            if (inner == null) continue;
                            double mag = 0.0; string hem = string.Empty; string icao = string.Empty;
                            if (inner.ContainsKey("mag")) mag = Convert.ToDouble(inner["mag"]);
                            if (inner.ContainsKey("hem")) hem = inner["hem"] as string ?? string.Empty;
                            if (inner.ContainsKey("icao")) icao = inner["icao"] as string ?? string.Empty;
                            _aptBaseMag[kv.Key] = new AptBaseInfo { Mag = mag, Hem = hem, IcaoId = icao };
                        }
                        catch { }
                    }
                }
                Log($"LoadCache: Parsed {_runwayData.Count} airports from cache");
                // If cache provided runway data but no meta/source was present, set a sensible fallback
                if ((_runwayData != null && _runwayData.Count > 0) && string.IsNullOrEmpty(LastLoadedSource))
                {
                    LastLoadedSource = "(cached)";
                    LastLoadedUtc = DateTime.UtcNow;
                }
                // restore metadata
                try
                {
                    if (obj.TryGetValue("meta", out var metaObj) && metaObj is Dictionary<string, object> metaDict)
                    {
                        if (metaDict.TryGetValue("source", out var s)) LastLoadedSource = s as string;
                        if (metaDict.TryGetValue("utc", out var u) && u is string us)
                        {
                            if (DateTime.TryParse(us, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) LastLoadedUtc = dt;
                        }
                    }
                }
                catch { }

                // Populate magnetic variation and other airport-level data into each runway from _aptBaseMag
                if (_aptBaseMag != null && _aptBaseMag.Count > 0)
                {
                    foreach (var kv in _runwayData)
                    {
                        string airportId = kv.Key;
                        if (_aptBaseMag.TryGetValue(airportId, out var aptInfo))
                        {
                            foreach (var r in kv.Value)
                            {
                                r.MagneticVariationDeg = aptInfo.Mag;
                                r.MagneticHemisphere = aptInfo.Hem;
                                r.IcaoId = aptInfo.IcaoId;
                            }
                        }
                    }
                    Log($"LoadCache: Applied magnetic variation data to {_runwayData.Count} airports");
                }
            }
            catch (Exception ex)
            {
                string msg = $"LoadCache: EXCEPTION - {ex.GetType().Name}: {ex.Message}\r\nStack: {ex.StackTrace}";
                try { if (logPath != null) File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n"); } catch { }
                System.Diagnostics.Debug.WriteLine(msg);
            }
        }

        // Public wrapper to attempt loading the cache on demand. Returns true if data present after load.
        public bool EnsureCacheLoaded()
        {
            try
            {
                LoadCache();
            }
            catch { }
            return (_runwayData != null && _runwayData.Count > 0);
        }

        private static string NormalizeRunwayId(string rwy)
        {
            if (string.IsNullOrEmpty(rwy)) return rwy;
            rwy = rwy.Trim().Trim('"');
            // Split number and optional side letter
            string numPart = new string(rwy.TakeWhile(char.IsDigit).ToArray());
            string rest = rwy.Substring(numPart.Length);
            int num;
            if (int.TryParse(numPart, out num))
            {
                // 1..36 normalized without leading zero, keep 2 digits if present in data sometimes
                num = ((num - 1) % 36) + 1; // keep in range though input should be valid
                numPart = num.ToString(CultureInfo.InvariantCulture);
            }
            rest = rest.ToUpperInvariant();
            // Map possible variants
            if (rest == "LEFT") rest = "L";
            else if (rest == "RIGHT") rest = "R";
            else if (rest == "CENTER" || rest == "CENTRE") rest = "C";
            return numPart + rest;
        }
    }
}
