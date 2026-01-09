using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using CommonPlayniteShared.Common;
using CommonPluginsShared;
using CommonPluginsShared.Extensions;
using CommonPluginsShared.Models;
using CommonPluginsStores;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using SuccessStory.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SuccessStory.Clients
{
    public class ExophaseAchievements : GenericAchievements
    {
        #region Urls

        private string UrlApi => @"https://api.exophase.com";
        private string UrlExophaseSearch => UrlApi + "/public/archive/games?q={0}&sort=added";
        private string UrlExophaseSearchPlatform => UrlApi + "/public/archive/platform/{1}?q={0}&sort=added";

        private string UrlExophase => @"https://www.exophase.com";
        private string UrlExophaseLogin => $"{UrlExophase}/login";
        private string UrlExophaseLogout => $"{UrlExophase}/logout";
        private string UrlExophaseAccount => $"{UrlExophase}/account";

        #endregion

        public static List<string> Platforms = new List<string>
        {
            ResourceProvider.GetString("LOCAll"),
            "Apple",
            "Blizzard",
            "Electronic Arts",
            "Epic",
            "GOG",
            "Google Play",
            "Nintendo",
            "PSN",
            "Retro",
            "Stadia",
            "Steam",
            "Ubisoft",
            "Xbox"
        };


        public ExophaseAchievements() : base("Exophase")
        {
            CookiesDomains = new List<string> { ".exophase.com" };
		}


        public override GameAchievements GetAchievements(Game game)
        {
            throw new NotImplementedException();
        }

        public GameAchievements GetAchievements(Game game, string url)
        {
            return GetAchievements(game, new SearchResult { Name = game.Name, Url = url });
        }

        public GameAchievements GetAchievements(Game game, SearchResult searchResult)
        {
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            List<Achievement> allAchievements = new List<Achievement>();

            try
            {
                string dataExophaseLocalised = string.Empty;
                string dataExophase = string.Empty;

                // Normalize fetch URL and prepare cache key
                string fetchUrl = searchResult.Url ?? string.Empty;
                if (fetchUrl.StartsWith("/"))
                {
                    fetchUrl = UrlExophase.TrimEnd('/') + fetchUrl;
                }
                if (!fetchUrl.Contains("/achievements", StringComparison.InvariantCultureIgnoreCase))
                {
                    fetchUrl = fetchUrl.TrimEnd('/') + "/achievements/";
                }
                string cacheKeyUrl = fetchUrl;
                try
                {
                    var cacheDir = Path.Combine(PluginDatabase.Paths.PluginCachePath, "ExophaseImages");
                    if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                    string cacheKey = Regex.Replace(cacheKeyUrl ?? string.Empty, "[^a-zA-Z0-9_-]", "_");
                    if (cacheKey.Length > 100) cacheKey = Common.Helper.GetMd5Hash(cacheKeyUrl);
                    string cacheFile = Path.Combine(cacheDir, cacheKey + ".json");
                    if (File.Exists(cacheFile))
                    {
                        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
                        if (age.TotalDays <= 30)
                        {
                            // load cached achievements quickly
                            var jsonCache = File.ReadAllText(cacheFile);
                            try
                            {
                                var cached = Serialization.FromJson<Dictionary<string, Achievement>>(jsonCache);
                                if (cached != null && cached.Count > 0)
                                {
                                    gameAchievements.Items = cached.Values.ToList();
                                    // Register images to resolver
                                    var imagesDict = cached.ToDictionary(kv => kv.Key, kv => kv.Value.UrlUnlocked);
                                    Services.AchievementImageResolver.RegisterImages(game, imagesDict);
                                    return gameAchievements;
                                }
                            }
                            catch
                            {
                                // fall back to old format if deserialization fails
                                var cachedOld = Serialization.FromJson<Dictionary<string, string>>(jsonCache);
                                if (cachedOld != null && cachedOld.Count > 0)
                                {
                                    var cachedList = cachedOld.Select(kv => new Achievement { Name = kv.Key, UrlUnlocked = kv.Value }).ToList();
                                    gameAchievements.Items = cachedList;
                                    Services.AchievementImageResolver.RegisterImages(game, cachedOld);
                                    return gameAchievements;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false, "Error reading Exophase cache", true, PluginDatabase.PluginName);
                }

                try
                {
                    // Force synchronous WebView fetch first to obtain fully rendered page (bypasses Cloudflare/JS)
                    try
                    {
                        var webData = Web.DownloadSourceDataWebView(fetchUrl, GetCookies(), true, CookiesDomains).GetAwaiter().GetResult();
                        if (!string.IsNullOrEmpty(webData.Item1))
                        {
                            dataExophase = webData.Item1;
                        }
                    }
                    catch (Exception)
                    {
                        // WebView fetch failed; fall back to HTTP methods
                        dataExophase = string.Empty;
                    }

                    if (string.IsNullOrEmpty(dataExophase))
                    {
                        // Fallback: try HTTP client then simple download as before
                        string fetched = null;
                        using (var httpClient = new HttpClient())
                        {
                            httpClient.Timeout = TimeSpan.FromSeconds(15);
                            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");

                            try
                            {
                                var resp = httpClient.GetAsync(fetchUrl).GetAwaiter().GetResult();
                                if (resp.IsSuccessStatusCode)
                                {
                                    fetched = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                }
                            }
                            catch (Exception)
                            {
                                // HTTP client fetch failed; allow fallback
                            }
                        }

                        if (!string.IsNullOrEmpty(fetched))
                        {
                            dataExophase = fetched;
                        }
                        else
                        {
                            try
                            {
                                dataExophase = Web.DownloadStringData(fetchUrl).GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                Common.LogError(ex, false, $"Exophase HTTP fetch failed for {searchResult.Url}, scheduling background WebView fetch", true, PluginDatabase.PluginName);

                                // Background fetch: parse, cache and register images without blocking
                                ScheduleBackgroundFetch(fetchUrl, searchResult.Url, game);

                                dataExophase = string.Empty;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false, "Error fetching Exophase page", true, PluginDatabase.PluginName);
                    dataExophase = string.Empty;
                }

                // Check if the fetched page contains achievement lists; some Exophase pages don't include 'Achievements' in <title>
                bool hasAchievementLists = false;
                if (!string.IsNullOrEmpty(dataExophase))
                {
                    try
                    {
                        var parserCheck = new HtmlParser();
                        var docCheck = parserCheck.Parse(dataExophase);
                        hasAchievementLists = (docCheck.QuerySelectorAll("ul.achievement, ul.trophy, ul.challenge")?.Length ?? 0) > 0;
                    }
                    catch (Exception)
                    {
                        hasAchievementLists = false;
                    }
                }

                if (!hasAchievementLists)
                {
                    try
                    {
                        // Try a blocking WebView fetch to get the fully rendered page (may bypass Cloudflare / JS rendering)
                        var webData = Web.DownloadSourceDataWebView(fetchUrl, GetCookies(), true, CookiesDomains).GetAwaiter().GetResult();
                        if (!string.IsNullOrEmpty(webData.Item1))
                        {
                            try
                            {
                                var parserCheck2 = new HtmlParser();
                                var docCheck2 = parserCheck2.Parse(webData.Item1);
                                if ((docCheck2.QuerySelectorAll("ul.achievement, ul.trophy, ul.challenge")?.Length ?? 0) > 0)
                                {
                                    dataExophase = webData.Item1;
                                    hasAchievementLists = true;
                                }
                            }
                            catch (Exception)
                            {
                                // fall through to schedule background fetch
                            }
                        }

                        if (!hasAchievementLists)
                        {
                            // Schedule background WebView fetch and skip blocking retrieval
                            ScheduleBackgroundFetch(fetchUrl, searchResult.Url, game);

                            dataExophase = string.Empty;
                        }
                    }
                    catch (Exception exWeb)
                    {
                        Common.LogError(exWeb, false, $"Exophase synchronous WebView fetch failed for {searchResult.Url}; scheduling background fetch", true, PluginDatabase.PluginName);
                        // Fallback to background fetch
                        ScheduleBackgroundFetch(fetchUrl, searchResult.Url, game);

                        dataExophase = string.Empty;
                    }
                }

                if (PluginDatabase.PluginSettings.Settings.UseLocalised && !IsConnected())
                {
                    Logger.Warn($"Exophase is disconnected");
                    string message = string.Format(ResourceProvider.GetString("LOCCommonStoresNoAuthenticate"), ClientName);
                    API.Instance.Notifications.Add(new NotificationMessage(
                        $"{PluginDatabase.PluginName}-Exophase-disconnected",
                        $"{PluginDatabase.PluginName}\r\n{message}",
                        NotificationType.Error,
                        () => PluginDatabase.Plugin.OpenSettingsView()
                    ));
                }
                else if (PluginDatabase.PluginSettings.Settings.UseLocalised)
                {
                     try
                     {
                         dataExophaseLocalised = Web.DownloadStringData(fetchUrl).GetAwaiter().GetResult();
                     }
                     catch (Exception ex)
                     {
                         Common.LogError(ex, false, $"Exophase localized fetch failed for {searchResult.Url}", true, PluginDatabase.PluginName);
                         dataExophaseLocalised = string.Empty;
                     }
                    // If localized page contains a notice message, skip retries
                    if (!string.IsNullOrEmpty(dataExophaseLocalised) && dataExophaseLocalised.Contains("Notice Message App"))
                    {
                        // skip additional retries
                    }
                 }

                List<Achievement> All = ParseData(dataExophase);
                List<Achievement> AllLocalised = dataExophaseLocalised.IsNullOrEmpty() ? new List<Achievement>() : ParseData(dataExophaseLocalised);

                // After parsing, cache images to disk for future runs
                try
                {
                    var imagesDict = All.Where(a => !a.Name.IsNullOrEmpty() && !a.UrlUnlocked.IsNullOrEmpty()).ToDictionary(a => a.Name, a => a.UrlUnlocked);
                    if (imagesDict.Count > 0)
                    {
                        var cacheDir = Path.Combine(PluginDatabase.Paths.PluginCachePath, "ExophaseImages");
                        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
                        string cacheKey = Regex.Replace(searchResult.Url ?? string.Empty, "[^a-zA-Z0-9_-]", "_");
                        if (cacheKey.Length > 100) cacheKey = Common.Helper.GetMd5Hash(searchResult.Url);
                        string cacheFile = Path.Combine(cacheDir, cacheKey + ".json");
                        File.WriteAllText(cacheFile, Serialization.ToJson(imagesDict));
                        Services.AchievementImageResolver.RegisterImages(game, imagesDict);
                    }
                }
                catch (Exception exCache)
                {
                    Common.LogError(exCache, false, "Error caching Exophase images", true, PluginDatabase.PluginName);
                }

                for (int i = 0; i < All?.Count; i++)
                {
                    allAchievements.Add(new Achievement
                    {
                        Name = AllLocalised.Count > 0 ? AllLocalised[i].Name : All[i].Name,
                        ApiName = All[i].Name,
                        UrlUnlocked = All[i].UrlUnlocked,
                        Description = AllLocalised.Count > 0 ? AllLocalised[i].Description : All[i].Description,
                        DateUnlocked = All[i].DateUnlocked,
                        Percent = All[i].Percent,
                        GamerScore = All[i].GamerScore
                    });
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }

            gameAchievements.Items = allAchievements;

            // Set source link
            if (gameAchievements.HasAchievements)
            {
                gameAchievements.SourcesLink = new SourceLink
                {
                    GameName = searchResult.Name.IsNullOrEmpty() ? searchResult.Name : searchResult.Name,
                    Name = "Exophase",
                    Url = searchResult.Url
                };
            }

            return gameAchievements;
        }


        #region Configuration

        public override bool ValidateConfiguration()
        {
            // The authentification is only for localised achievement
            return true;
        }

        public override bool IsConnected()
        {
            if (CachedIsConnectedResult == null)
            {
                CachedIsConnectedResult = GetIsUserLoggedIn();
            }
            return (bool)CachedIsConnectedResult;
        }

        public override bool EnabledInSettings()
        {
            // No necessary activation
            return true;
        }

        #endregion

        #region Exophase

        public void Login()
        {
            FileSystem.DeleteFile(CookiesPath);
            ResetCachedIsConnectedResult();

            WebViewSettings webViewSettings = new WebViewSettings
            {
                WindowWidth = 580,
                WindowHeight = 700,
                JavaScriptEnabled = true,
                // This is needed otherwise captcha won't pass
                UserAgent = Web.UserAgent
            };

            using (IWebView webView = API.Instance.WebViews.CreateView(webViewSettings))
            {
                webView.LoadingChanged += (s, e) =>
                {
                    string address = webView.GetCurrentAddress();
                    if (address.StartsWith(UrlExophaseAccount, StringComparison.InvariantCultureIgnoreCase) && !address.StartsWith(UrlExophaseLogout, StringComparison.InvariantCultureIgnoreCase))
                    {
                        CachedIsConnectedResult = true;
                        webView.Close();
                    }
                };

                webView.DeleteDomainCookies(CookiesDomains.First());
                webView.Navigate(UrlExophaseLogin);
                _ = webView.OpenDialog();
            }

            // Wait for cookies to be flushed to disk
            Thread.Sleep(2000);
            List<HttpCookie> httpCookies = CookiesTools.GetWebCookies(true);
            
            if (httpCookies.Count > 0)
            {
                SetCookies(httpCookies);
            }
            else
            {
                Logger.Warn("Exophase Login: No cookies found after login.");
            }
        }

        private bool GetIsUserLoggedIn()
        {
            var data = Web.DownloadSourceDataWebView(UrlExophaseAccount, GetCookies(), true, CookiesDomains).GetAwaiter().GetResult();
            bool isConnected = data.Item1.Contains("column-username", StringComparison.InvariantCultureIgnoreCase);

            if (isConnected)
            {
                SetCookies(data.Item2);
            }

            return isConnected;
        }


        public List<SearchResult> SearchGame(string name, string platforms = "")
        {
            List<SearchResult> listSearchGames = new List<SearchResult>();
            try
			{
				string urlSearch = platforms.IsNullOrEmpty() || platforms.IsEqual(ResourceProvider.GetString("LOCAll"))
					? string.Format(UrlExophaseSearch, WebUtility.UrlEncode(name))
					: string.Format(UrlExophaseSearchPlatform, WebUtility.UrlEncode(name), platforms);

                var dataText = Web.DownloadJsonDataWebView(urlSearch, GetCookies()).GetAwaiter().GetResult();
				string json = dataText.Item1;

                if (!Serialization.TryFromJson(json, out ExophaseSearchResult exophaseScheachResult))
                {
                    Logger.Warn($"No Exophase result for {name}");
                    Logger.Warn($"{json}");
                    return listSearchGames;
                }

                List<List> listExophase = exophaseScheachResult?.Games?.List;
                if (listExophase != null)
                {
                    listSearchGames = listExophase.Select(x => new SearchResult
                    {
                        Url = x.EndpointAwards,
                        Name = x.Title,
                        UrlImage = x.Images.O ?? x.Images.L ?? x.Images.M,
                        Platforms = x.Platforms.Select(p => p.Name).ToList(),
                        AchievementsCount = x.TotalAwards ?? 0
                    }).ToList();
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, $"Error on SearchGame({name})", true, PluginDatabase.PluginName);
            }

            return listSearchGames;
        }


        private string GetAchievementsPageUrl(GameAchievements gameAchievements, Services.SuccessStoryDatabase.AchievementSource source)
        {
            bool usedSplit = false;

            string sourceLinkName = gameAchievements.SourcesLink?.Name;
            if (sourceLinkName == "Exophase")
            {
                return gameAchievements.SourcesLink.Url;
            }

            List<SearchResult> searchResults = SearchGame(gameAchievements.Name);
            if (searchResults.Count == 0)
            {
                Logger.Warn($"No game found for {gameAchievements.Name} in GetAchievementsPageUrl()");

                Thread.Sleep(1000);
                searchResults = SearchGame(CommonPluginsShared.PlayniteTools.NormalizeGameName(gameAchievements.Name));
                if (searchResults.Count == 0)
                {
                    Logger.Warn($"No game found for {CommonPluginsShared.PlayniteTools.NormalizeGameName(gameAchievements.Name)} in GetAchievementsPageUrl()");

                    Thread.Sleep(1000);
                    searchResults = SearchGame(Regex.Match(gameAchievements.Name, @"^.*(?=[:-])").Value);
                    usedSplit = true;
                    if (searchResults.Count == 0)
                    {
                        Logger.Warn($"No game found for {Regex.Match(gameAchievements.Name, @"^.*(?=[:-])").Value} in GetAchievementsPageUrl()");
                        return null;
                    }
                }
            }

            string normalizedGameName = usedSplit ? CommonPluginsShared.PlayniteTools.NormalizeGameName(Regex.Match(gameAchievements.Name, @"^.*(?=[:-])").Value) : CommonPluginsShared.PlayniteTools.NormalizeGameName(gameAchievements.Name);
            SearchResult searchResult = searchResults.Find(x => CommonPluginsShared.PlayniteTools.NormalizeGameName(x.Name) == normalizedGameName && PlatformAndProviderMatch(x, gameAchievements, source));

            if (searchResult == null)
            {
                Logger.Warn($"No matching game found for {gameAchievements.Name} in GetAchievementsPageUrl()");
            }

            return searchResult?.Url;
        }


        /// <summary>
        /// Set achievement rarity via Exophase web scraping.
        /// </summary>
        /// <param name="gameAchievements"></param>
        /// <param name="source"></param>
        public void SetRarety(GameAchievements gameAchievements, Services.SuccessStoryDatabase.AchievementSource source)
        {
            string achievementsUrl = GetAchievementsPageUrl(gameAchievements, source);
            if (achievementsUrl.IsNullOrEmpty())
            {
                Logger.Warn($"No Exophase (rarity) url found for {gameAchievements.Name} - {gameAchievements.Id}");
                return;
            }

            try
            {
                GameAchievements exophaseAchievements = GetAchievements(gameAchievements.Game, achievementsUrl);
                exophaseAchievements.Items.ForEach(y =>
                {
                    Achievement achievement = gameAchievements.Items.Find(x => x.ApiName.IsEqual(y.ApiName));
                    if (achievement == null)
                    {
                        achievement = gameAchievements.Items.Find(x => x.Name.IsEqual(y.Name));
                        if (achievement == null)
                        {
                            achievement = gameAchievements.Items.Find(x => x.Name.IsEqual(y.ApiName));
                        }
                    }

                    if (achievement != null)
                    {
                        achievement.ApiName = y.ApiName;
                        achievement.Percent = y.Percent;
                        achievement.GamerScore = StoreApi.CalcGamerScore(y.Percent);

                        if (PluginDatabase.PluginSettings.Settings.UseLocalised && IsConnected())
                        {
                            achievement.Name = y.Name;
                            achievement.Description = y.Description;
                        }
                    }
                    else
                    {
                        Logger.Warn($"No Exophase (rarity) matching achievements found for {gameAchievements.Name} - {gameAchievements.Id} - {y.Name} in {achievementsUrl}");
                    }
                });

                var missingMatches = new List<string>();
                foreach (var y in exophaseAchievements.Items)
                {
                    Achievement achievement = gameAchievements.Items.Find(x => x.ApiName.IsEqual(y.ApiName));
                    if (achievement == null)
                    {
                        achievement = gameAchievements.Items.Find(x => x.Name.IsEqual(y.Name));
                        if (achievement == null)
                        {
                            achievement = gameAchievements.Items.Find(x => x.Name.IsEqual(y.ApiName));
                        }
                    }

                    if (achievement != null)
                    {
                        achievement.ApiName = y.ApiName;
                        achievement.Percent = y.Percent;
                        achievement.GamerScore = StoreApi.CalcGamerScore(y.Percent);

                        if (PluginDatabase.PluginSettings.Settings.UseLocalised && IsConnected())
                        {
                            achievement.Name = y.Name;
                            achievement.Description = y.Description;
                        }
                    }
                    else
                    {
                        // Collect missing names and log a single summary after processing to avoid flooding logs
                        try
                        {
                            if (!string.IsNullOrEmpty(y.Name))
                            {
                                missingMatches.Add(y.Name);
                            }
                        }
                        catch { }
                    }
                }

                if (missingMatches.Count > 0)
                {
                    // limit output length
                    int maxShow = 10;
                    string sample = string.Join(", ", missingMatches.Take(maxShow));
                    if (missingMatches.Count > maxShow)
                    {
                        sample += ", ...";
                    }
                    Logger.Warn($"No Exophase (rarity) matching achievements found for {gameAchievements.Name} - {gameAchievements.Id} in {achievementsUrl}: {missingMatches.Count} missing. Examples: {sample}");
                }

                PluginDatabase.AddOrUpdate(gameAchievements);
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }
        }

        private static bool PlatformAndProviderMatch(SearchResult exophaseGame, GameAchievements playniteGame, Services.SuccessStoryDatabase.AchievementSource achievementSource)
        {
            switch (achievementSource)
            {
                //PC: match service
                case Services.SuccessStoryDatabase.AchievementSource.Steam:
                    return exophaseGame.Platforms.Contains("Steam", StringComparer.InvariantCultureIgnoreCase);

                case Services.SuccessStoryDatabase.AchievementSource.GOG:
                    return exophaseGame.Platforms.Contains("GOG", StringComparer.InvariantCultureIgnoreCase);

                case Services.SuccessStoryDatabase.AchievementSource.EA:
                    return exophaseGame.Platforms.Contains("Electronic Arts", StringComparer.InvariantCultureIgnoreCase);

                case Services.SuccessStoryDatabase.AchievementSource.RetroAchievements:
                    return exophaseGame.Platforms.Contains("Retro", StringComparer.InvariantCultureIgnoreCase);

                case Services.SuccessStoryDatabase.AchievementSource.Overwatch:
                case Services.SuccessStoryDatabase.AchievementSource.Starcraft2:
                case Services.SuccessStoryDatabase.AchievementSource.Wow:
                    return exophaseGame.Platforms.Contains("Blizzard", StringComparer.InvariantCultureIgnoreCase);

                //Console: match platform
                case Services.SuccessStoryDatabase.AchievementSource.Playstation:
                case Services.SuccessStoryDatabase.AchievementSource.Xbox:
                case Services.SuccessStoryDatabase.AchievementSource.RPCS3:
                    return PlatformsMatch(exophaseGame, playniteGame);

                case Services.SuccessStoryDatabase.AchievementSource.Epic:
                case Services.SuccessStoryDatabase.AchievementSource.GenshinImpact:
                case Services.SuccessStoryDatabase.AchievementSource.GuildWars2:
                case Services.SuccessStoryDatabase.AchievementSource.None:
                case Services.SuccessStoryDatabase.AchievementSource.Local:
                default:
                    return false;
            }
        }

        private static Dictionary<string, string[]> PlaynitePlatformSpecificationIdToExophasePlatformName => new Dictionary<string, string[]>
        {
            { "xbox360", new[]{"Xbox 360"} },
            { "xbox_one", new[]{"Xbox One"} },
            { "xbox_series", new[]{"Xbox Series"} },
            { "xbox_game_pass", new []{"Windows 8", "Windows 10", "Windows 11", "GFWL", "Xbox 360", "Xbox One", "Xbox Series" } },
            { "pc_windows", new []{"Windows 8", "Windows 10", "Windows 11" /* future proofing */, "GFWL"} },
            { "sony_playstation3", new[]{"PS3"} },
            { "sony_playstation4", new[]{"PS4"} },
            { "sony_playstation5", new[]{"PS5"} },
            { "sony_vita", new[]{"PS Vita"} },
        };

        private static bool PlatformsMatch(SearchResult exophaseGame, GameAchievements playniteGame)
        {
            foreach (Platform playnitePlatform in playniteGame.Platforms)
            {
                string sourceName = string.Empty;
                string key = string.Empty;
                try
                {
                    sourceName = API.Instance.Database.Games.Get(playniteGame.Id)?.Source?.Name;
                    key = sourceName == "Xbox Game Pass" ? "xbox_game_pass" : playnitePlatform.SpecificationId;
                    if (!PlaynitePlatformSpecificationIdToExophasePlatformName.TryGetValue(key, out string[] exophasePlatformNames))
                    {
                        continue;
                    }

                    if (exophaseGame?.Platforms?.IntersectsExactlyWith(exophasePlatformNames) ?? false)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, false, $"Error on PlatformsMatch with {sourceName} - {key}");
                }
            }
            return false;
        }

        #endregion


        private List<Achievement> ParseData(string data)
        {
            HtmlParser parser = new HtmlParser();
            IHtmlDocument htmlDocument = parser.Parse(data);

            List<Achievement> allAchievements = new List<Achievement>();
            IHtmlCollection<IElement> sectionAchievements = htmlDocument.QuerySelectorAll("ul.achievement, ul.trophy, ul.challenge");
            string gameName = htmlDocument.QuerySelector("h2.me-2 a")?.GetAttribute("title");

            if (sectionAchievements == null || sectionAchievements.Count() == 0)
            {
                Logger.Warn("Exophase data is not parsed");
                return new List<Achievement>();
            }
            else
            {
                foreach (IElement section in sectionAchievements)
                {
                    foreach (IElement searchAchievements in section.QuerySelectorAll("li"))
                    {
                        try
                        {
                            string sFloat = searchAchievements.GetAttribute("data-average")
                                ?.Replace(".", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator)
                                ?.Replace(",", CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);

                            _ = float.TryParse(sFloat, out float Percent);

                            string urlUnlocked = searchAchievements.QuerySelector("img")?.GetAttribute("src");
                            string name = WebUtility.HtmlDecode(searchAchievements.QuerySelector("a")?.InnerHtml);
                            string description = WebUtility.HtmlDecode(searchAchievements.QuerySelector("div.award-description p")?.InnerHtml);
                            bool isHidden = searchAchievements.GetAttribute("class").IndexOf("secret") > -1;

                            allAchievements.Add(new Achievement
                            {
                                Name = name,
                                UrlUnlocked = urlUnlocked,
                                Description = description,
                                DateUnlocked = default(DateTime),
                                Percent = Percent,
                                GamerScore = StoreApi.CalcGamerScore(Percent)
                            });
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false, true, PluginDatabase.PluginName);
                        }
                    }
                }
            }

            return allAchievements;
        }


        private static readonly SemaphoreSlim _bgFetchSemaphore = new SemaphoreSlim(2);

        private void ScheduleBackgroundFetch(string fetchUrl, string searchResultUrl, Game game)
        {
            Task.Run(async () =>
            {
                await _bgFetchSemaphore.WaitAsync();
                try
                {
                    var webDataBg = await Web.DownloadSourceDataWebView(fetchUrl, GetCookies(), false, CookiesDomains);
                    if (webDataBg.Item1.IsNullOrEmpty())
                    {
                        Logger.Warn($"Exophase background fetch: no data from {fetchUrl}");
                        return;
                    }

                    var parsed = ParseData(webDataBg.Item1);
                    CacheAndApplyImages(parsed, searchResultUrl, game);
                }
                catch (Exception bgEx)
                {
                    Common.LogError(bgEx, false, $"Exophase background fetch failed for {searchResultUrl}", true, PluginDatabase.PluginName);
                }
                finally
                {
                    _bgFetchSemaphore.Release();
                }
            });
        }

        private void CacheAndApplyImages(List<Achievement> parsed, string searchResultUrl, Game game)
        {
            // Filter out empty Name/UrlUnlocked and de-duplicate by Name
            var achievementsDict = new Dictionary<string, Achievement>();
            var imagesDict = new Dictionary<string, string>();
            var imagesNormalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in parsed)
            {
                if (a.Name.IsNullOrEmpty()) continue;
                if (!achievementsDict.ContainsKey(a.Name))
                {
                    achievementsDict.Add(a.Name, a);
                    if (!a.UrlUnlocked.IsNullOrEmpty())
                    {
                        imagesDict.Add(a.Name, a.UrlUnlocked);
                        
                        string keyNorm = a.Name.RemoveDiacritics().ToLowerInvariant().Trim();
                        if (!imagesNormalized.ContainsKey(keyNorm))
                        {
                            imagesNormalized.Add(keyNorm, a.UrlUnlocked);
                        }
                    }
                }
            }

            if (achievementsDict.Count > 0)
            {
                var cacheDir = Path.Combine(PluginDatabase.Paths.PluginCachePath, "ExophaseImages");
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                string cacheKey = Regex.Replace(searchResultUrl ?? string.Empty, "[^a-zA-Z0-9_-]", "_");
                if (cacheKey.Length > 100) cacheKey = Common.Helper.GetMd5Hash(searchResultUrl);
                string cacheFile = Path.Combine(cacheDir, cacheKey + ".json");
                File.WriteAllText(cacheFile, Serialization.ToJson(achievementsDict));

                if (imagesDict.Count > 0)
                {
                    Services.AchievementImageResolver.RegisterImages(game, imagesDict);
                }

                try
                {
                    // Also apply images to any existing GameAchievements in the plugin DB so UI updates immediately
                    var existing = PluginDatabase.Get(game, true);
                    if (existing != null && existing.Items != null && existing.Items.Count > 0)
                    {
                        bool changed = false;
                        foreach (var it in existing.Items)
                        {
                            if (it == null) continue;
                            string name = it.Name ?? string.Empty;
                            string keyNorm = name.RemoveDiacritics().ToLowerInvariant().Trim();
                            
                            if (keyNorm.IsNullOrEmpty()) continue;

                            if (imagesNormalized.TryGetValue(keyNorm, out string url))
                            {
                                if (it.UrlUnlocked != url)
                                {
                                    it.UrlUnlocked = url;
                                    changed = true;
                                }
                            }
                        }

                        if (changed)
                        {
                            PluginDatabase.AddOrUpdate(existing);
                        }
                    }
                }
                catch (Exception exUpdate)
                {
                    Common.LogError(exUpdate, false, $"Exophase background: failed to apply images to DB for {game.Name}", true, PluginDatabase.PluginName);
                }
            }
        }
    }
}