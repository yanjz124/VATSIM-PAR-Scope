using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        private const string NASR_BASE_URL = "https://nfdc.faa.gov/webContent/28DaySub/extra/";
        private Dictionary<string, List<RunwayEndData>> _runwayData;
        public string LastLoadedSource { get; private set; }

        public class RunwayEndData
        {
            public string AirportId { get; set; }
            public string RunwayId { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double TrueHeading { get; set; }
            public double FieldElevationFt { get; set; }
            public double ThrCrossingHgtFt { get; set; }
        }

        public NASRDataLoader()
        {
            _runwayData = new Dictionary<string, List<RunwayEndData>>(StringComparer.OrdinalIgnoreCase);
            LastLoadedSource = null;
            // Try to load cached data from AppData
            try { LoadCache(); } catch { }
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
            
            List<RunwayEndData> runways;
            if (!_runwayData.TryGetValue(airportId.ToUpperInvariant(), out runways))
                return null;

            if (string.IsNullOrEmpty(runwayId))
                return runways.FirstOrDefault();

            string want = NormalizeRunwayId(runwayId);
            return runways.FirstOrDefault(r =>
                NormalizeRunwayId(r.RunwayId).Equals(want, StringComparison.OrdinalIgnoreCase));
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
            return _runwayData.Keys.Where(k => !string.IsNullOrEmpty(k) && k.All(ch => char.IsLetter(ch))).ToList();
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
                    // Find APT_RWY_END.csv
                    ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => 
                        e.Name.Equals("APT_RWY_END.csv", StringComparison.OrdinalIgnoreCase));

                    if (entry == null)
                    {
                        errorMessage = "APT_RWY_END.csv not found in archive";
                        return false;
                    }

                    // Parse CSV
                    using (Stream stream = entry.Open())
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        return ParseRunwayEndCsv(reader, out errorMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                errorMessage = "Error reading zip file: " + ex.Message;
                return false;
            }
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
                    if (cols.Length <= Math.Max(Math.Max(colAirportId, rwyCol),
                                                Math.Max(colLat, Math.Max(colLon, colTrueHdg))))
                        continue;

                    try
                    {
                        string airportId = cols[colAirportId].Trim().Trim('"');
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

                        var data = new RunwayEndData
                        {
                            AirportId = airportId,
                            RunwayId = runwayId,
                            Latitude = lat,
                            Longitude = lon,
                            TrueHeading = hdg,
                            FieldElevationFt = elev,
                            ThrCrossingHgtFt = tch
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

        private string GetCachePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "VATSIM-PAR-Scope");
            try { Directory.CreateDirectory(folder); } catch { }
            return Path.Combine(folder, "nasr_cache.json");
        }

        private void SaveCache()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                var dump = _runwayData.ToDictionary(k => k.Key, v => v.Value);
                string json = ser.Serialize(dump);
                File.WriteAllText(GetCachePath(), json, Encoding.UTF8);
            }
            catch { }
        }

        private void LoadCache()
        {
            try
            {
                string path = GetCachePath();
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var obj = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (obj == null) return;
                _runwayData.Clear();
                foreach (var kv in obj)
                {
                    var list = new List<RunwayEndData>();
                    var arr = kv.Value as object[];
                    if (arr == null) continue;
                    foreach (var it in arr)
                    {
                        var map = it as Dictionary<string, object>;
                        if (map == null) continue;
                        var r = new RunwayEndData();
                        r.AirportId = map.ContainsKey("AirportId") ? (string)map["AirportId"] : kv.Key;
                        r.RunwayId = map.ContainsKey("RunwayId") ? (string)map["RunwayId"] : null;
                        r.Latitude = map.ContainsKey("Latitude") ? Convert.ToDouble(map["Latitude"]) : 0;
                        r.Longitude = map.ContainsKey("Longitude") ? Convert.ToDouble(map["Longitude"]) : 0;
                        r.TrueHeading = map.ContainsKey("TrueHeading") ? Convert.ToDouble(map["TrueHeading"]) : 0;
                        r.FieldElevationFt = map.ContainsKey("FieldElevationFt") ? Convert.ToDouble(map["FieldElevationFt"]) : 0;
                        r.ThrCrossingHgtFt = map.ContainsKey("ThrCrossingHgtFt") ? Convert.ToDouble(map["ThrCrossingHgtFt"]) : 0;
                        list.Add(r);
                    }
                    _runwayData[kv.Key] = list;
                }
            }
            catch { }
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
