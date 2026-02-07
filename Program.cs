using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Books.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace LibrisScan.Crawler
{
    /// <summary>
    /// LibrisScan: A utility to automatically fetch book metadata and covers 
    /// from Google Books API based on local eBook filenames.
    /// </summary>
    class Program
    {
        #region Configuration & State
        // Global paths derived from the application execution folder
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string CredentialsPath = Path.Combine(BaseDir, "GoogleApiAuth", "google-credentials.json");
        private static readonly string LogFilePath = Path.Combine(BaseDir, "Logs", "processed_files.log");
        private static List<string> SearchFilters = new List<string>();

        // Runtime directories loaded from appsettings.json
        private static string JsonDir = string.Empty;
        private static string CoversDir = string.Empty;
        private static string EbookSourceDir = string.Empty;

        // Shared state and statistics
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly HashSet<string> ProcessedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Stopwatch stopWatch = new Stopwatch();

        private static bool isQuotaExhausted = false;
        private static int countTotal, countLogSkipped, countIsbnSkipped, countSaved;
        #endregion

        static async Task Main(string[] args)
        {
            try
            {
                // Initialize folders, logs, and configuration
                SetupEnvironment();

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("==================================================");
                Console.WriteLine("       LibrisScan: Metadata Crawler Active        ");
                Console.WriteLine("==================================================");
                Console.ResetColor();

                // Authentication is required for the BooksService to function
                if (!File.Exists(CredentialsPath))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FATAL] Google Credentials not found at: {CredentialsPath}");
                    Console.ResetColor();
                    return;
                }

                var service = await AuthenticateService();

                // Recursively gather all PDF and EPUB files
                var files = Directory.GetFiles(EbookSourceDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)).ToList();

                countTotal = files.Count;
                int currentIdx = 0;

                foreach (var filePath in files)
                {
                    currentIdx++;
                    UpdateTitle(currentIdx);

                    if (isQuotaExhausted)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine("\n[!] Quota exhausted. Stopping scan...");
                        Console.ResetColor();
                        break;
                    }

                    // Skip files already successfully logged
                    if (ProcessedFiles.Contains(filePath))
                    {
                        countLogSkipped++;
                        // Optional: Print small indicator for skipped files to show activity
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"[{currentIdx}/{countTotal}] Skipping: {Path.GetFileName(filePath)} (Already Processed)");
                        Console.ResetColor();
                        continue;
                    }

                    // Highlight the file currently being searched
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($"\n[{currentIdx}/{countTotal}] Searching: {Path.GetFileNameWithoutExtension(filePath)}");
                    Console.ResetColor();

                    // Step 1: Search for the book by its filename
                    var isbns = await GetIsbnsFromSearch(service, Path.GetFileNameWithoutExtension(filePath));

                    if (isbns.Count == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"   [?] No ISBNs found for this title.");
                        Console.ResetColor();
                    }
                    else
                    {
                        // Step 2: For every found ISBN, try to download metadata and covers
                        foreach (var isbn in isbns)
                        {
                            if (isQuotaExhausted) break;

                            Console.Write($"   [→] Processing ISBN: {isbn}... ");

                            // Check if we already have this metadata on disk
                            string jsonFileName = $"{isbn}.json";
                            string jsonPath = Path.Combine(JsonDir, jsonFileName); // Ensure JsonDir is accessible here

                            if (File.Exists(jsonPath))
                            {
                                Console.ForegroundColor = ConsoleColor.DarkCyan;
                                Console.WriteLine("Exist");
                                Console.ResetColor();
                                continue; // Move to the next ISBN without calling the API
                            }

                            // If it doesn't exist, try to download it
                            if (await ProcessIsbn(isbn))
                            {
                                countSaved++;
                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine("Saved!");
                                Console.ResetColor();
                            }
                            else
                            {
                                // If ProcessIsbn returns false, it was either a network error or quota hit
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine("Error (API Fail or Quota)");
                                Console.ResetColor();
                            }

                            await Task.Delay(2500); // Respectful delay
                        }
                    }

                    // Log this file as 'handled'
                    SaveToLog(filePath);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
                Console.ResetColor();
            }

            Finish();
        }

        #region Core Logic Methods
        /// <summary>
        /// Cleans the filename and queries Google Books to find valid ISBNs.
        /// </summary>
        private static async Task<List<string>> GetIsbnsFromSearch(BooksService service, string fileName)
        {
            string safeFileName = fileName ?? string.Empty;

            // Build the regex pattern dynamically from the external SearchFilters list.
            // Use an empty pattern "$^" (which matches nothing) if no filters are provided.
            string pattern = (SearchFilters != null && SearchFilters.Any())
                ? string.Join("|", SearchFilters)
                : "$^";

            // Apply the dynamically built regex to clean the filename for the API search
            var cleanQuery = Regex.Replace(safeFileName, pattern, "", RegexOptions.IgnoreCase).Trim();

            var list = new List<string>();
            try
            {
                var request = service.Volumes.List(cleanQuery);
                request.MaxResults = 10;
                var results = await request.ExecuteAsync();

                if (results.Items != null)
                {
                    foreach (var item in results.Items)
                    {
                        var identifiers = item.VolumeInfo?.IndustryIdentifiers;
                        if (identifiers == null) continue;

                        foreach (var id in identifiers)
                        {
                            if (string.IsNullOrEmpty(id.Identifier)) continue;

                            // Normalize all results to ISBN_13 for consistency
                            if (id.Type == "ISBN_13") list.Add(id.Identifier);
                            else if (id.Type == "ISBN_10") list.Add(ConvertIsbn10To13(id.Identifier));
                        }
                    }
                }
            }
            catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 429 || (int)ex.HttpStatusCode == 403)
            {
                isQuotaExhausted = true; // Mark quota as full to stop further requests
            }
            catch (Exception ex) { Console.WriteLine($"   [SEARCH ERROR] {ex.Message}"); }

            return list.Distinct().ToList();
        }

        /// <summary>
        /// Fetches full JSON metadata and the high-res thumbnail for a specific ISBN.
        /// </summary>
        private static async Task<bool> ProcessIsbn(string isbn)
        {
            string path = Path.Combine(JsonDir, $"{isbn}.json");
            if (File.Exists(path)) { countIsbnSkipped++; return false; }

            try
            {
                var resp = await httpClient.GetAsync($"https://www.googleapis.com/books/v1/volumes?q=isbn:{isbn}");
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode == 403) isQuotaExhausted = true;
                    return false;
                }

                string body = await resp.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    // Persist the full raw JSON for future local parsing
                    File.WriteAllText(path, body);

                    var info = items[0].GetProperty("volumeInfo");
                    if (info.TryGetProperty("imageLinks", out var links) && links.TryGetProperty("thumbnail", out var thumbProp))
                    {
                        var thumb = thumbProp.GetString();
                        if (!string.IsNullOrEmpty(thumb))
                        {
                            // Upgrade to HTTPS to ensure successful download
                            thumb = thumb.Replace("http://", "https://");
                            var imgBytes = await httpClient.GetByteArrayAsync(thumb);
                            File.WriteAllBytes(Path.Combine(CoversDir, $"{isbn}.jpg"), imgBytes);
                        }
                    }

                    //Console.ForegroundColor = ConsoleColor.Green;
                    //Console.WriteLine($"    [✔] Saved: {isbn}");
                    //Console.ResetColor();
                    return true;
                }
            }
            catch { } // Silently skip failures for individual ISBNs to keep the batch moving
            return false;
        }
        #endregion

        #region Infrastructure & Helpers
        /// <summary>
        /// Updates the Console Title bar with a real-time progress percentage.
        /// </summary>
        private static void UpdateTitle(int current) =>
            Console.Title = $"({(double)current / countTotal:P0}) LibrisScan | {current}/{countTotal} | Saved: {countSaved}";

        /// <summary>
        /// Handles directory creation, config generation, and loading the processed files log.
        /// </summary>
        private static void SetupEnvironment()
        {
            var logDir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);

            string configPath = Path.Combine(BaseDir, "appsettings.json");
            if (!File.Exists(configPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[CONFIG] Creating default appsettings.json using BaseDirectory...");

                string safeBaseDir = BaseDir.Replace("\\", "\\\\");

                // Note: The margin of the raw string is defined by the leftmost character of the closing triple quotes below
                string defaultConfig = $$"""
                                        {
                                            "StorageSettings": {
                                            "JsonMetadataDir": "{{safeBaseDir}}GoogleBooks\\Metadata",
                                            "CoversDir": "{{safeBaseDir}}GoogleBooks\\Covers",
                                            "EbookSourceDir": "{{safeBaseDir}}E-Books Collections"
                                            },
                                            "SearchFilters": [
                                            "\\(Z-Library\\)",
                                            "-- Anna['’]s Archive",
                                            "- libgen\\.li",
                                            "\\(z-library\\.sk, 1lib\\.sk, z-lib\\.sk\\)"
                                            ]
                                        }
                                        """;

                File.WriteAllText(configPath, defaultConfig);
                Console.ResetColor();
            }

            var config = new ConfigurationBuilder()
                .SetBasePath(BaseDir)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            JsonDir = config["StorageSettings:JsonMetadataDir"] ?? Path.Combine(BaseDir, "Metadata");
            CoversDir = config["StorageSettings:CoversDir"] ?? Path.Combine(BaseDir, "Covers");
            EbookSourceDir = config["StorageSettings:EbookSourceDir"] ?? Path.Combine(BaseDir, "Ebooks");
            SearchFilters = config.GetSection("SearchFilters").Get<List<string>>() ?? new List<string>();

            Directory.CreateDirectory(JsonDir);
            Directory.CreateDirectory(CoversDir);

            if (File.Exists(LogFilePath))
            {
                foreach (var line in File.ReadAllLines(LogFilePath))
                {
                    if (!string.IsNullOrWhiteSpace(line)) ProcessedFiles.Add(line.Trim());
                }
            }

            stopWatch.Start();
        }

        private static void SaveToLog(string path) => File.AppendAllLines(LogFilePath, new[] { path });

        /// <summary>
        /// Clears the console and provides a final summary report of the session.
        /// </summary>
        private static void Finish()
        {
            stopWatch.Stop();
            Console.Clear();
            Console.Title = isQuotaExhausted ? "LibrisScan - STOPPED (Quota)" : "LibrisScan - COMPLETED";

            Console.WriteLine("=============================================");
            Console.WriteLine("           LIBRISSCAN FINAL SUMMARY          ");
            if (isQuotaExhausted)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("   *** DAILY GOOGLE QUOTA EXHAUSTED *** ");
                Console.ResetColor();
            }
            Console.WriteLine("=============================================");
            Console.WriteLine($"Time Elapsed: {stopWatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"Total Files Found:  {countTotal}");
            Console.WriteLine($"Skipped (By Log):   {countLogSkipped}");
            Console.WriteLine($"Newly Downloaded:   {countSaved}");
            Console.WriteLine("=============================================");
            Console.WriteLine("\nDone. Press any key to exit.");
            Console.ReadKey();
        }

        /// <summary>
        /// Standard algorithm to convert legacy 10-digit ISBNs to the modern 13-digit format.
        /// </summary>
        private static string ConvertIsbn10To13(string isbn10)
        {
            if (isbn10.Length != 10) return isbn10;
            string t = "978" + isbn10.Substring(0, 9);
            int s = 0;
            for (int i = 0; i < t.Length; i++)
                s += (t[i] - '0') * (i % 2 == 0 ? 1 : 3);

            return t + ((10 - (s % 10)) % 10);
        }

        /// <summary>
        /// Initializes the OAuth2 or API Key flow for Google Services.
        /// </summary>
        private static async Task<BooksService> AuthenticateService()
        {
            using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read);
            var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                new[] { BooksService.Scope.Books },
                "user",
                CancellationToken.None,
                new FileDataStore(Path.Combine(BaseDir, "GoogleApiAuth", "token_store"), true));

            return new BooksService(new BaseClientService.Initializer
            {
                HttpClientInitializer = cred,
                ApplicationName = "LibrisScan"
            });
        }
        #endregion
    }
}