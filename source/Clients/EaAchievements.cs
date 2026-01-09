using Playnite.SDK.Models;
using CommonPluginsShared;
using CommonPluginsShared.Models;
using SuccessStory.Models;
using System;
using System.Collections.Generic;
using static CommonPluginsShared.PlayniteTools;
using CommonPluginsStores.Ea;
using System.Collections.ObjectModel;
using CommonPluginsStores.Models;
using System.Linq;
using Playnite.SDK;
using System.Threading.Tasks;
using CommonPluginsShared.Extensions;
using System.Text.RegularExpressions;

namespace SuccessStory.Clients
{
    public class EaAchievements : GenericAchievements
    {
        protected static readonly Lazy<EaApi> eaApi = new Lazy<EaApi>(() => new EaApi(PluginDatabase.PluginName));
        internal static EaApi EaApi => eaApi.Value;


        public EaAchievements() : base("EA", CodeLang.GetEaLang(API.Instance.ApplicationSettings.Language), CodeLang.GetCountryFromLast(API.Instance.ApplicationSettings.Language))
        {
            EaApi.SetLanguage(API.Instance.ApplicationSettings.Language);
        }


        public override GameAchievements GetAchievements(Game game)
        {
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            List<Achievement> AllAchievements = new List<Achievement>();

            if (IsConnected())
            {
                try
                {
                    ObservableCollection<GameAchievement> originAchievements = Task.Run(() =>
                    {
                        var result = EaApi.GetAchievements(game.GameId, EaApi.CurrentAccountInfos);
                        return result;
                    }).GetAwaiter().GetResult();

                    if (originAchievements?.Count > 0)
                    {
                        AllAchievements = originAchievements.Select(x => new Achievement
                        {
                            ApiName = x.Id,
                            Name = x.Name,
                            Description = x.Description,
                            UrlUnlocked = x.UrlUnlocked,
                            UrlLocked = x.UrlLocked,
                            DateUnlocked = x.DateUnlocked.ToString().Contains(default(DateTime).ToString()) ? (DateTime?)null : x.DateUnlocked,
                            Percent = x.Percent,
                            GamerScore = x.GamerScore
                        }).ToList();
                        gameAchievements.Items = AllAchievements;
                    }

                    // Set source link
                    if (gameAchievements.HasAchievements)
                    {
                        gameAchievements.SourcesLink = new SourceLink
                        {
                            GameName = game.Name,
                            Name = "EA",
                            Url = "https://www.ea.com"
                        };

                        // Only fetch external images if none of the achievements already include images
                        if (gameAchievements.Items != null && gameAchievements.Items.Any(x => !x.UrlUnlocked.IsNullOrEmpty()))
                        {
                            // Achievements already contain images, skip external fetch
                        }
                        else
                        {
                                // 0) Try to get images from the resolver cache first
                                if (Services.AchievementImageResolver.TryGetImages(game, out images) && images?.Count > 0)
                                {
                                    Logger.Info($"Found {images.Count} images in resolver cache for {game.Name}");
                                }

                                // 1) Try Exophase first (it's known to have EA images sometimes)
                                if (images.Count == 0)
                                {
                                    try
                                    {
                                         if (SuccessStory.ExophaseAchievements != null)
                                         {
                                        // Try with platform filter first
                                        var exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name, "Electronic Arts");

                                        if (exSearch == null || exSearch.Count == 0)
                                        {
                                            // Fallback to generic search
                                            exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name);
                                        }

                                        if (exSearch?.Count > 0)
                                            {
                                            // Prefer a result that includes Electronic Arts platform
                                            SearchResult exMatch = exSearch.FirstOrDefault(x => x.Platforms != null && x.Platforms.Any(p => p.Equals("Electronic Arts", StringComparison.InvariantCultureIgnoreCase)));

                                            // Prefer PC/origin entries
                                            if (exMatch == null)
                                            {
                                                exMatch = exSearch.FirstOrDefault(s => prefersPc(s));
                                            }

                                            // Filter out console-specific entries (e.g. Switch) when possible
                                            var exSearchFiltered = exSearch.Where(x => !( (x.Name ?? string.Empty).IndexOf("switch", StringComparison.InvariantCultureIgnoreCase) >= 0 || (x.Name ?? string.Empty).IndexOf("nintendo", StringComparison.InvariantCultureIgnoreCase) >= 0 || (x.Platforms != null && x.Platforms.Any(p => p.IndexOf("switch", StringComparison.InvariantCultureIgnoreCase) >= 0 || p.IndexOf("nintendo", StringComparison.InvariantCultureIgnoreCase) >= 0)) )).ToList();
                                            if (exSearchFiltered.Count > 0)
                                            {
                                                // try to find EA or PC in filtered set
                                                if (exMatch == null)
                                                {
                                                    exMatch = exSearchFiltered.FirstOrDefault(x => x.Platforms != null && x.Platforms.Any(p => p.Equals("Electronic Arts", StringComparison.InvariantCultureIgnoreCase)));
                                                }
                                                if (exMatch == null)
                                                {
                                                    exMatch = exSearchFiltered.FirstOrDefault(s => prefersPc(s));
                                                }
                                                if (exMatch == null)
                                                {
                                                    string normalizedGame = NormalizeGameName(game.Name);
                                                    exMatch = exSearchFiltered.FirstOrDefault(x => NormalizeGameName(x.Name).IsEqual(normalizedGame));
                                                }
                                                if (exMatch == null)
                                                {
                                                    exMatch = exSearchFiltered.First();
                                                }
                                            }

                                            // If still null, try exact normalized name match on original list
                                            if (exMatch == null)
                                            {
                                                string normalizedGame = NormalizeGameName(game.Name);
                                                exMatch = exSearch.FirstOrDefault(x => NormalizeGameName(x.Name).IsEqual(normalizedGame));
                                            }

                                            // Fallback to first result
                                            if (exMatch == null)
                                            {
                                                exMatch = exSearch.First();
                                            }

                                            if (!exMatch.Url.IsNullOrEmpty())
                                            {
                                                // Ensure URL is absolute
                                                string exUrl = exMatch.Url;
                                                if (exUrl.StartsWith("/"))
                                                {
                                                    exUrl = "https://www.exophase.com" + exUrl;
                                                }
                                                // Get achievements page parsed by Exophase (includes urls)
                                                var exAch = SuccessStory.ExophaseAchievements.GetAchievements(game, exUrl);
                                                if (exAch?.Items?.Count > 0)
                                                {
                                                     string exBase = "https://www.exophase.com";
                                                     foreach (var i in exAch.Items.Where(i => !i.UrlUnlocked.IsNullOrEmpty()))
                                                     {
                                                         string key = i.Name ?? string.Empty;
                                                         string urlImg = i.UrlUnlocked;
                                                         if (urlImg.StartsWith("/"))
                                                         {
                                                             urlImg = exBase + urlImg;
                                                         }
                                                         if (!images.ContainsKey(key) && !string.IsNullOrEmpty(urlImg))
                                                         {
                                                             images.Add(key, urlImg);
                                                         }
                                                     }
                                                 }
                                             }
                                         }
                                     }
                                }
                                catch (Exception exSearch)
                                {
                                    Common.LogError(exSearch, false, "Error while searching Exophase for EA images", true, PluginDatabase.PluginName);
                                }

                                // 2) If still no images, try TrueAchievements - Xbox origin
                                if (images.Count == 0)
                                {
                                    var taGames = TrueAchievements.SearchGame(game, TrueAchievements.OriginData.Xbox);
                                    if (taGames.Count > 0)
                                    {
                                        var match = taGames.First();
                                        if (!match.GameUrl.IsNullOrEmpty())
                                        {
                                            images = TrueAchievements.GetDataImages(match.GameUrl);
                                        }
                                    }
                                }

                                // 3) Fallback TrueAchievements - Steam origin if still no images
                                if (images.Count == 0)
                                {
                                    try
                                    {
                                        var taGamesSteam = TrueAchievements.SearchGame(game, TrueAchievements.OriginData.Steam);
                                        if (taGamesSteam.Count > 0)
                                        {
                                            var match = taGamesSteam.First();
                                            if (!match.GameUrl.IsNullOrEmpty())
                                            {
                                                images = TrueAchievements.GetDataImages(match.GameUrl);
                                            }
                                        }
                                    }
                                    catch (Exception exTa)
                                    {
                                        Common.LogError(exTa, false, "Error while searching TrueAchievements for EA images", true, PluginDatabase.PluginName);
                                    }
                                }

                                // If we have images, try to map them to achievements with tolerant matching
                                if (images.Count > 0)
                                {
                                    // Register with resolver so other components skip redundant lookups
                                    try
                                    {
                                        Services.AchievementImageResolver.RegisterImages(game, images);
                                    }
                                    catch (Exception exReg)
                                    {
                                        Common.LogError(exReg, false, "Error registering achievement images", true, PluginDatabase.PluginName);
                                    }

                                    // Build normalized dictionary for images
                                    var imagesNormalized = new Dictionary<string, string>();
                                    foreach (var kv in images)
                                    {
                                        string keyNorm = normalize(kv.Key);
                                        if (!keyNorm.IsNullOrEmpty() && !imagesNormalized.ContainsKey(keyNorm))
                                        {
                                            imagesNormalized.Add(keyNorm, kv.Value);
                                        }
                                    }

                                    int mapped = 0;
                                    foreach (var ach in gameAchievements.Items)
                                    {
                                        string achNorm = normalize(ach.Name);
                                        bool assigned = false;

                                        // 1) exact normalized match
                                        if (!achNorm.IsNullOrEmpty() && imagesNormalized.TryGetValue(achNorm, out string imgUrl))
                                        {
                                            ach.UrlUnlocked = imgUrl;
                                            ach.UrlLocked = imgUrl;
                                            mapped++;
                                            assigned = true;
                                        }

                                        if (!assigned)
                                        {
                                            // 2) match by ApiName normalized
                                            string apiNorm = normalize(ach.ApiName);
                                            if (!apiNorm.IsNullOrEmpty() && imagesNormalized.TryGetValue(apiNorm, out imgUrl))
                                            {
                                                ach.UrlUnlocked = imgUrl;
                                                ach.UrlLocked = imgUrl;
                                                mapped++;
                                                assigned = true;
                                            }
                                        }

                                        if (!assigned && !achNorm.IsNullOrEmpty())
                                        {
                                            // 3) startsWith / contains on normalized keys
                                            var found = imagesNormalized.FirstOrDefault(x => achNorm.StartsWith(x.Key) || x.Key.StartsWith(achNorm) || achNorm.Contains(x.Key) || x.Key.Contains(achNorm));
                                            if (!found.Equals(default(KeyValuePair<string, string>)))
                                            {
                                                ach.UrlUnlocked = found.Value;
                                                ach.UrlLocked = found.Value;
                                                mapped++;
                                                assigned = true;
                                            }
                                        }

                                        if (!assigned && !ach.Name.IsNullOrEmpty())
                                        {
                                            // 4) word overlap heuristic: split into words and try to find image key containing at least half of words
                                            var achWords = ach.Name.RemoveDiacritics().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r', ',', ':', '-', '–' }, StringSplitOptions.RemoveEmptyEntries).Select(w => Regex.Replace(w, "[^a-z0-9]", "")).Where(w => w.Length > 2).ToList();
                                            if (achWords.Count > 0)
                                            {
                                                foreach (var kv in imagesNormalized)
                                                {
                                                    int overlap = achWords.Count(w => kv.Key.Contains(w));
                                                    if (overlap >= Math.Ceiling(achWords.Count / 2.0))
                                                    {
                                                        ach.UrlUnlocked = kv.Value;
                                                        ach.UrlLocked = kv.Value;
                                                        mapped++;
                                                        assigned = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Common.LogError(ex, false, true, PluginDatabase.PluginName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, $"EaAchievements: Error fetching achievements for {game.Name}");
                    ShowNotificationPluginError(ex);
                    return gameAchievements;
                }
            }
            else
            {
                Logger.Warn($"EaAchievements: Not connected when fetching for {game.Name}");
                ShowNotificationPluginNoAuthenticate(ExternalPlugin.OriginLibrary);
            }

            gameAchievements.SetRaretyIndicator();
            PluginDatabase.AddOrUpdate(gameAchievements);
            return gameAchievements;
        }


        #region Configuration
        public override bool ValidateConfiguration()
        {
            if (!PluginDatabase.PluginSettings.Settings.PluginState.OriginIsEnabled)
            {
                ShowNotificationPluginDisable(ResourceProvider.GetString("LOCSuccessStoryNotificationsEaDisabled"));
                return false;
            }
            else
            {
                if (CachedConfigurationValidationResult == null)
                {
                    CachedConfigurationValidationResult = IsConnected();

                    if (!(bool)CachedConfigurationValidationResult)
                    {
                        ShowNotificationPluginNoAuthenticate(ExternalPlugin.OriginLibrary);
                    }
                }
                else if (!(bool)CachedConfigurationValidationResult)
                {
                    ShowNotificationPluginErrorMessage(ExternalPlugin.OriginLibrary);
                }

                return (bool)CachedConfigurationValidationResult;
            }
        }

        public override bool IsConnected()
        {
            if (CachedIsConnectedResult == null)
            {
                try
                {
                    CachedIsConnectedResult = EaApi.IsUserLoggedIn;
                }
                catch (Exception ex)
                {
                    Common.LogError(ex, true);
                    CachedIsConnectedResult = false;
                }
            }
            
            return (bool)CachedIsConnectedResult;
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableOrigin;
        }

        public override void ResetCachedConfigurationValidationResult()
        {
            CachedConfigurationValidationResult = null;
            EaApi.ResetIsUserLoggedIn();
        }

        public override void ResetCachedIsConnectedResult()
        {
            CachedIsConnectedResult = null;
            EaApi.ResetIsUserLoggedIn();
        }
        #endregion
    }
}
