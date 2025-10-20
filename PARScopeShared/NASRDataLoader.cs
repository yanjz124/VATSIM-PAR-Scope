using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PARScopeShared
{
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
            public string RwyIdCsv { get; set; }
            public string ApchLgtSystemCode { get; set; }
        }

        public NASRDataLoader()
        {
            _runwayData = new Dictionary<string, List<RunwayEndData>>(StringComparer.OrdinalIgnoreCase);
            LastLoadedSource = null;
            try { LoadCache(); } catch { }
        }

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
                    SaveCache();
                }
                return ok;
            }
            catch (Exception ex)
            {
                errorMessage = "Error loading file: " + ex.Message;
                return false;
            }
        }

        public async Task<bool> TryLoadLatestDataAsync()
        {
            // simplified async variant for demo
            string lastError = null;
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i <= 28; i++)
            {
                DateTime d = now.AddDays(-i);
                string zipFilename = ConstructZipFilename(d);
                string url = NASR_BASE_URL + zipFilename;
                bool ok = await TryDownloadAndParseAsync(url);
                if (ok)
                {
                    LastLoadedSource = url;
                    SaveCache();
                    return true;
                }
            }
            return false;
        }

        private string ConstructZipFilename(DateTime date)
        {
            string day = date.Day.ToString("D2");
            string month = date.ToString("MMM", CultureInfo.InvariantCulture);
            string year = date.Year.ToString();
            return string.Format("{0}_{1}_{2}_APT_CSV.zip", day, month, year);
        }

        private async Task<bool> TryDownloadAndParseAsync(string url)
        {
            try
            {
                using var http = new HttpClient();
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return false;
                using var ms = new MemoryStream();
                await resp.Content.CopyToAsync(ms);
                ms.Position = 0;
                // write temp file
                string temp = Path.GetTempFileName();
                try
                {
                    using (var fs = File.OpenWrite(temp))
                    {
                        ms.CopyTo(fs);
                        fs.Flush();
                    }
                    bool ok = ParseZipFile(temp, out var err);
                    return ok;
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }
            }
            catch
            {
                return false;
            }
        }

        private bool ParseZipFile(string zipPath, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("APT_RWY_END.csv", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) { errorMessage = "APT_RWY_END.csv not found"; return false; }
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    return ParseRunwayEndCsv(reader, out errorMessage);
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
                string header = reader.ReadLine();
                if (header == null) { errorMessage = "Empty CSV"; return false; }
                string[] headerCols = SplitCsvLine(header);
                Func<string, string> norm = s => (s ?? string.Empty).Trim().Trim('"').Replace(" ", "_").ToUpperInvariant();
                string[] headerNorm = headerCols.Select(norm).ToArray();

                int colAirportId = Array.FindIndex(headerNorm, c => c == "ARPT_ID" || c == "ARPT_IDENT" || c == "AIRPORT_ID" || c == "ARPT");
                int colRunwayEndId = Array.FindIndex(headerNorm, c => c == "RWY_END_ID" || c == "RWY_END_IDENT");
                int colRunwayPairId = Array.FindIndex(headerNorm, c => c == "RWY_ID" || c == "RUNWAY_ID");
                int colLat = Array.FindIndex(headerNorm, c => (c.Contains("RWY_END") && c.Contains("LAT")) || c == "LAT_DECIMAL");
                int colLon = Array.FindIndex(headerNorm, c => (c.Contains("RWY_END") && (c.Contains("LONG") || c.Contains("LON"))) || c == "LONG_DECIMAL" || c == "LON_DECIMAL");
                int colTrueHdg = Array.FindIndex(headerNorm, c => c == "TRUE_ALIGNMENT" || (c.Contains("TRUE") && (c.Contains("ALIGN") || c.Contains("BEARING") || c.Contains("BRG") || c.Contains("HDG"))));

                if (colAirportId < 0 || (colRunwayEndId < 0 && colRunwayPairId < 0) || colLat < 0 || colLon < 0 || colTrueHdg < 0)
                {
                    errorMessage = "Required columns not found";
                    return false;
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] cols = SplitCsvLine(line);
                    int rwyCol = colRunwayEndId >= 0 ? colRunwayEndId : colRunwayPairId;
                    if (cols.Length <= Math.Max(Math.Max(colAirportId, rwyCol), Math.Max(colLat, Math.Max(colLon, colTrueHdg))))
                        continue;
                    try
                    {
                        string airportId = cols[colAirportId].Trim().Trim('"');
                        string runwayId = null;
                        if (colRunwayEndId >= 0 && colRunwayEndId < cols.Length)
                            runwayId = cols[colRunwayEndId].Trim().Trim('"');
                        else if (colRunwayPairId >= 0 && colRunwayPairId < cols.Length)
                        {
                            string pair = cols[colRunwayPairId].Trim().Trim('"');
                            if (!string.IsNullOrEmpty(pair)) { int slash = pair.IndexOf('/'); runwayId = slash >= 0 ? pair.Substring(0, slash) : pair; }
                        }
                        if (string.IsNullOrEmpty(airportId) || string.IsNullOrEmpty(runwayId)) continue;

                        if (!double.TryParse(cols[colLat].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)) continue;
                        if (!double.TryParse(cols[colLon].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)) continue;
                        if (!double.TryParse(cols[colTrueHdg].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double hdg)) continue;

                        var data = new RunwayEndData
                        {
                            AirportId = airportId,
                            RunwayId = runwayId,
                            Latitude = lat,
                            Longitude = lon,
                            TrueHeading = hdg
                        };
                        if (!_runwayData.TryGetValue(airportId, out var list)) { list = new List<RunwayEndData>(); _runwayData[airportId] = list; }
                        list.Add(data);
                    }
                    catch { continue; }
                }
                if (_runwayData.Count == 0) { errorMessage = "No runway data"; return false; }
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
            if (line == null) return Array.Empty<string>();
            var list = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (ch == ',' && !inQuotes) { list.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
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
                var dump = _runwayData.ToDictionary(k => k.Key, v => v.Value);
                string json = JsonSerializer.Serialize(dump);
                string cachePath = GetCachePath();
                File.WriteAllText(cachePath, json, Encoding.UTF8);
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
                var map = JsonSerializer.Deserialize<Dictionary<string, List<RunwayEndData>>>(json);
                if (map != null) _runwayData = map.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        private static string NormalizeRunwayId(string rwy)
        {
            if (string.IsNullOrEmpty(rwy)) return rwy;
            rwy = rwy.Trim().Trim('"');
            string numPart = new string(rwy.TakeWhile(char.IsDigit).ToArray());
            string rest = rwy.Substring(numPart.Length);
            if (!int.TryParse(numPart, out int num)) num = 0;
            rest = rest.ToUpperInvariant();
            if (rest == "LEFT") rest = "L";
            else if (rest == "RIGHT") rest = "R";
            else if (rest == "CENTER" || rest == "CENTRE") rest = "C";
            return (num > 0 ? num.ToString(CultureInfo.InvariantCulture) : numPart) + rest;
        }
    }
}
