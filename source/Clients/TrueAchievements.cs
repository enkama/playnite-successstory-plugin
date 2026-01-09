using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using CommonPluginsShared;
using CommonPluginsShared.Extensions;
using Playnite.SDK;
using Playnite.SDK.Models;
using SuccessStory.Services;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.IO;

namespace SuccessStory.Clients
{
    public class TrueAchievements
    {
        private static ILogger Logger => LogManager.GetLogger();

        internal static SuccessStoryDatabase PluginDatabase => SuccessStory.PluginDatabase;

        private static string XboxUrlSearch => @"https://www.trueachievements.com/searchresults.aspx?search={0}";
        private static string SteamUrlSearch => @"https://truesteamachievements.com/searchresults.aspx?search={0}";

        private const int MaxImagesPerPage = 500;
        private const int MaxImageNameLength = 120;

        public enum OriginData { Steam, Xbox }


        /// <summary>
        /// Search list game on truesteamachievements or trueachievements.
        /// </summary>
        /// <param name="game"></param>
        /// <param name="originData"></param>
        /// <returns></returns>
        public static List<TrueAchievementSearch> SearchGame(Game game, OriginData originData)
        {
            List<TrueAchievementSearch> listSearchGames = new List<TrueAchievementSearch>();
            string url;
            string urlBase;
            if (originData == OriginData.Steam)
            {
                //TODO: Decide if editions should be removed here
                url = string.Format(SteamUrlSearch, WebUtility.UrlEncode(PlayniteTools.NormalizeGameName(game.Name, true)));
                urlBase = @"https://truesteamachievements.com";
            }
            else
            {
                //TODO: Decide if editions should be removed here
                url = string.Format(XboxUrlSearch, WebUtility.UrlEncode(PlayniteTools.NormalizeGameName(game.Name, true)));
                urlBase = @"https://www.trueachievements.com";
            }


            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                var sourceData = Web.DownloadSourceDataWebView(url).GetAwaiter().GetResult();
                sw.Stop();
                Logger.Info($"SearchGame web request took {sw.ElapsedMilliseconds}ms for {url}");

                string response = sourceData.Item1;

                if (response.IsNullOrEmpty())
                {
                    Logger.Warn($"No data from {url}");
                    return listSearchGames;
                }

                HtmlParser parser = new HtmlParser();
                IHtmlDocument htmlDocument = parser.Parse(response);

                if (response.IndexOf("There are no matching search results, please change your search terms") > -1)
                {
                    return listSearchGames;
                }

                IElement SectionGames = htmlDocument.QuerySelector("#oSearchResults");
                if (SectionGames == null)
                {
                    string gameUrl = htmlDocument.QuerySelector("link[rel=\"canonical\"]")?.GetAttribute("href");
                    string gameImage = htmlDocument.QuerySelector("div.info img")?.GetAttribute("src");

                    listSearchGames.Add(new TrueAchievementSearch
                    {
                        GameUrl = gameUrl,
                        GameName = game.Name,
                        GameImage = gameImage
                    });
                }
                else
                {
                    foreach (IElement searchGame in SectionGames.QuerySelectorAll("tr"))
                    {
                        try
                        {
                            IHtmlCollection<IElement> gameInfos = searchGame.QuerySelectorAll("td");
                            if (gameInfos.Count() > 2)
                            {
                                string gameUrl = urlBase + gameInfos[0].QuerySelector("a")?.GetAttribute("href");
                                string gameName = gameInfos[1].QuerySelector("a")?.InnerHtml;
                                string gameImage = urlBase + gameInfos[0].QuerySelector("a img")?.GetAttribute("src");

                                string itemType = gameInfos[2].InnerHtml;

                                if (itemType.IsEqual("game"))
                                {
                                    listSearchGames.Add(new TrueAchievementSearch
                                    {
                                        GameUrl = gameUrl,
                                        GameName = gameName,
                                        GameImage = gameImage
                                    });
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug(ex, $"Error processing search game result for {url}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }

            return listSearchGames;
        }


        /// <summary>
        /// Get the estimate time from game url on truesteamachievements or trueachievements.
        /// </summary>
        /// <param name="urlTrueAchievement"></param>
        /// <returns></returns>
        public static EstimateTimeToUnlock GetEstimateTimeToUnlock(string urlTrueAchievement)
        {
            EstimateTimeToUnlock estimateTimeToUnlock = new EstimateTimeToUnlock();

            if (urlTrueAchievement.IsNullOrEmpty())
            {
                Logger.Warn($"No url for GetEstimateTimeToUnlock()");
                return estimateTimeToUnlock;
            }

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                var sourceData = Web.DownloadSourceDataWebView(urlTrueAchievement).GetAwaiter().GetResult();
                sw.Stop();
                Logger.Info($"GetEstimateTimeToUnlock web request took {sw.ElapsedMilliseconds}ms for {urlTrueAchievement}");

                string response = sourceData.Item1;

                if (response.IsNullOrEmpty())
                {
                    Logger.Warn($"No data from {urlTrueAchievement}");
                    return estimateTimeToUnlock;
                }


                HtmlParser parser = new HtmlParser();
                IHtmlDocument htmlDocument = parser.Parse(response);

                int numberDataCount = 0;
                foreach (IElement SearchElement in htmlDocument.QuerySelectorAll("div.game div.l1 div"))
                {
                    string title = SearchElement.GetAttribute("title");
                    if (!title.IsNullOrEmpty() && (title == "Maximum TrueAchievement" || title == "Maximum TrueSteamAchievement"))
                    {
                        string data = SearchElement.InnerHtml;
                        _ = int.TryParse(Regex.Replace(data, "[^0-9]", ""), out numberDataCount);
                        break;
                    }
                }

                foreach (IElement SearchElement in htmlDocument.QuerySelectorAll("div.game div.l2 a"))
                {
                    string title = SearchElement.GetAttribute("title");
                    if (!title.IsNullOrEmpty() && title == "Estimated time to unlock all achievements")
                    {
                        string estimateTime = SearchElement.InnerHtml
                            .Replace("<i class=\"fa fa-hourglass-end\"></i>", string.Empty)
                            .Replace("<i class=\"fa fa-clock-o\"></i>", string.Empty)
                            .Trim();

                        int estimateTimeMin = 0;
                        int estimateTimeMax = 0;
                        int index = 0;
                        foreach (string item in estimateTime.Replace("h", string.Empty).Split('-'))
                        {
                            _ = index == 0 ? int.TryParse(item.Replace("+", string.Empty), out estimateTimeMin) : int.TryParse(item, out estimateTimeMax);
                            index++;
                        }

                        estimateTimeToUnlock = new EstimateTimeToUnlock
                        {
                            DataCount = numberDataCount,
                            EstimateTime = estimateTime,
                            EstimateTimeMin = estimateTimeMin,
                            EstimateTimeMax = estimateTimeMax
                        };
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }

            if (estimateTimeToUnlock.EstimateTimeMin == 0)
            {
                Logger.Warn($"No {(urlTrueAchievement.ToLower().Contains("truesteamachievements") ? "TrueSteamAchievements" : "TrueAchievements")} data found");
            }

            return estimateTimeToUnlock;
        }

        /// <summary>
        /// Extract achievement image name -> url pairs from a TrueAchievements/TrueSteamAchievements game page.
        /// </summary>
        public static Dictionary<string, string> GetDataImages(string gameUrl)
        {
            var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(gameUrl))
            {
                return images;
            }

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                var sourceData = Web.DownloadSourceDataWebView(gameUrl).GetAwaiter().GetResult();
                sw.Stop();
                Logger.Debug($"GetDataImages web request took {sw.ElapsedMilliseconds}ms for {gameUrl.Substring(0, Math.Min(100, gameUrl.Length))}{(gameUrl.Length > 100 ? "..." : "")}");
                

                string response = sourceData.Item1;
                if (response.IsNullOrEmpty())
                {
                    Logger.Warn($"GetDataImages: no data from {gameUrl}");
                    return images;
                }

                HtmlParser parser = new HtmlParser();
                IHtmlDocument doc = parser.Parse(response);

                Uri baseUri = null;
                try
                {
                    baseUri = new Uri(gameUrl);
                }
                catch (Exception exBaseUri)
                {
                    Logger.Debug($"GetDataImages: invalid base URI '{gameUrl}' - {exBaseUri.Message}");
                    baseUri = null;
                }

                // Prefer selectors that commonly contain achievements and images
                var candidateSelectors = new[]
                {
                    ".achievement",
                    ".achievements",
                    ".achievement-list",
                    "#achievements"
                };

                // Collect img elements
                var imgElements = new List<IElement>();
                foreach (var sel in candidateSelectors)
                {
                    try
                    {
                        var found = doc.QuerySelectorAll($"{sel} img");
                        if (found != null)
                        {
                            imgElements.AddRange(found);
                        }
                    }
                    catch (Exception exSel)
                    {
                        Logger.Debug($"GetDataImages: selector '{sel} img' caused an error: {exSel.Message}");
                    }
                }

                // Fallback: search main content areas if specific selectors failed
                if (!imgElements.Any())
                {
                    var mainElements = doc.QuerySelectorAll("main, #main, .main, #content, .content");
                    if (mainElements != null)
                    {
                        foreach (var main in mainElements)
                        {
                            imgElements.AddRange(main.QuerySelectorAll("img"));
                        }
                    }
                }

                var processedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (var img in imgElements)
                {
                    // Cap processed images to avoid performance issues on huge pages
                    if (index >= MaxImagesPerPage) break;

                    try
                    {
                        string src = img.GetAttribute("src") ?? img.GetAttribute("data-src") ?? img.GetAttribute("data-original");
                        if (string.IsNullOrEmpty(src)) continue;

                        // Make absolute
                        string imgUrl = src;
                        if (imgUrl.StartsWith("//"))
                        {
                            imgUrl = (baseUri?.Scheme ?? "https") + ":" + imgUrl;
                        }
                        else if (imgUrl.StartsWith("/"))
                        {
                            if (baseUri != null)
                                imgUrl = baseUri.GetLeftPart(UriPartial.Authority) + imgUrl;
                        }
                        else if (!imgUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) && baseUri != null)
                        {
                            // relative path
                            imgUrl = new Uri(baseUri, imgUrl).ToString();
                        }

                        // De-duplicate by URL
                        if (processedUrls.Contains(imgUrl)) continue;
                        processedUrls.Add(imgUrl);

                        // Determine a best-effort name/key for the image
                        string name = img.GetAttribute("alt") ?? img.GetAttribute("title");
                        if (string.IsNullOrEmpty(name))
                        {
                            // try nearby text nodes: parent, grandparent
                            var parent = img.ParentElement;
                            string txt = parent?.TextContent?.Trim();
                            if (string.IsNullOrEmpty(txt)) txt = parent?.ParentElement?.TextContent?.Trim();
                            if (!string.IsNullOrEmpty(txt))
                            {
                                // take first line and limit length
                                txt = txt.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                                if (!string.IsNullOrEmpty(txt)) name = txt.Length > MaxImageNameLength ? txt.Substring(0, MaxImageNameLength) : txt;
                            }
                        }

                        if (string.IsNullOrEmpty(name))
                        {
                            // fallback to filename
                            try { name = Path.GetFileNameWithoutExtension(new Uri(imgUrl).AbsolutePath); }
                            catch (Exception ex)
                            {
                                Logger.Debug($"GetDataImages: Failed to extract filename from URL: {ex.Message}");
                            }
                        }

                        // Normalize whitespace
                        name = Regex.Replace(name, "\\s+", " ").Trim();

                        // Ensure unique key
                        string key = name;
                        int dup = 1;
                        while (images.ContainsKey(key))
                        {
                            key = name + " (" + dup + ")";
                            dup++;
                        }

                        if (!string.IsNullOrEmpty(imgUrl))
                        {
                            images.Add(key, imgUrl);
                        }

                        index++;
                    }
                    catch (Exception exImg)
                    {
                        Logger.Debug(exImg, $"Error processing image element in GetDataImages for {gameUrl}");
                    }
                }
            }
            catch (Exception ex)
            {
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }

            return images;
        }
    }


    public class TrueAchievementSearch
    {
        public string GameUrl { get; set; }
        public string GameName { get; set; }
        public string GameImage { get; set; }
    }

    public class EstimateTimeToUnlock
    {
        public int DataCount { get; set; }
        public string EstimateTime { get; set; }
        public int EstimateTimeMin { get; set; }
        public int EstimateTimeMax { get; set; }
    }
}