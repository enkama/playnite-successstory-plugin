using Playnite.SDK.Models;
using CommonPluginsShared;
using SuccessStory.Models;
using System;
using System.Linq;
using static CommonPluginsShared.PlayniteTools;
using CommonPluginsStores.Gog;
using System.Collections.ObjectModel;
using CommonPluginsStores.Models;
using System.Collections.Generic;
using Playnite.SDK;
using FuzzySharp;
using CommonPluginsShared.Models;
using CommonPluginsShared.Extensions;

namespace SuccessStory.Clients
{
    public class GogAchievements : GenericAchievements
    {
        private GogApi GogApi => SuccessStory.GogApi;


        public GogAchievements() : base("GOG", CodeLang.GetGogLang(API.Instance.ApplicationSettings.Language))
        {
            // Null-safety: GogApi may not be initialized yet if called early in plugin lifecycle
            if (SuccessStory.GogApi != null)
            {
                SuccessStory.GogApi.SetLanguage(API.Instance.ApplicationSettings.Language);
            }
        }


        public override GameAchievements GetAchievements(Game game)
        {
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            List<Achievement> AllAchievements = new List<Achievement>();

            if (IsConnected())
            {
                try
                {
                    ObservableCollection<GameAchievement> gogAchievements = GogApi.GetAchievements(game.GameId, GogApi.CurrentAccountInfos);
                    if (gogAchievements?.Count > 0)
                    {
                        AllAchievements = gogAchievements.Select(x => new Achievement
                        {
                            ApiName = x.Id,
                            Name = x.Name,
                            Description = x.Description,
                            UrlUnlocked = x.UrlUnlocked,
                            UrlLocked = x.UrlLocked,
                            DateUnlocked = x.DateUnlocked.ToString().Contains(default(DateTime).ToString()) ? (DateTime?)null : x.DateUnlocked,
                            Percent = x.Percent,
                            GamerScore = x.GamerScore,
                            IsHidden = x.IsHidden
                        }).ToList();
                        gameAchievements.Items = AllAchievements;
                    }
                    else
                    {
                        // No achievements returned by GOG API — try Exophase as a fallback
                        try
                        {
                            if (SuccessStory.ExophaseAchievements != null)
                            {
                                Logger.Info($"GOG.GetAchievements: no achievements from GOG API for {game.Name}, trying Exophase fallback");
                                var exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name, "GOG");
                                if (exSearch == null || exSearch.Count == 0)
                                {
                                    exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name);
                                }

                                SearchResult exMatch = null;
                                if (exSearch?.Count > 0)
                                {
                                    string normalizedGame = NormalizeGameName(game.Name);

                                    var platformPreferred = exSearch.Where(x => x.Platforms != null && x.Platforms.Any(p => p.IsEqual("GOG"))).ToList();
                                    var candidates = platformPreferred.Count > 0 ? platformPreferred : exSearch;

                                    if (platformPreferred.Count == 0 && candidates.Count > 1)
                                    {
                                        var preferredUrlTokens = new[] { "gog", "pc", "windows", "steam", "epic" };
                                        var urlFiltered = candidates.Where(x => !string.IsNullOrEmpty(x.Url) && preferredUrlTokens.Any(t => x.Url.IndexOf(t, StringComparison.InvariantCultureIgnoreCase) >= 0)).ToList();
                                        if (urlFiltered.Count > 0)
                                        {
                                            candidates = urlFiltered;
                                        }
                                    }

                                    var scored = candidates.Select(x => new { Item = x, Score = Fuzz.TokenSetRatio(normalizedGame, NormalizeGameName(x.Name)) })
                                        .OrderByDescending(x => x.Score)
                                        .ToList();

                                    int threshold = 75;
                                    if (scored.First().Score >= threshold)
                                    {
                                        exMatch = scored.First().Item;
                                    }
                                    else
                                    {
                                        exMatch = exSearch.FirstOrDefault(x => NormalizeGameName(x.Name).IsEqual(normalizedGame));
                                        if (exMatch == null)
                                        {
                                            exMatch = scored.First().Item;
                                        }
                                    }
                                }

                                if (exMatch != null && !exMatch.Url.IsNullOrEmpty())
                                {
                                    string exUrl = exMatch.Url;
                                    if (exUrl.StartsWith("/"))
                                    {
                                        exUrl = "https://www.exophase.com" + exUrl;
                                    }

                                    var exAch = SuccessStory.ExophaseAchievements.GetAchievements(game, exUrl);
                                    if (exAch?.Items?.Count > 0)
                                    {
                                        AllAchievements = exAch.Items.Select(x => new Achievement
                                        {
                                            ApiName = x.ApiName ?? x.Name,
                                            Name = x.Name,
                                            Description = x.Description,
                                            UrlUnlocked = x.UrlUnlocked,
                                            UrlLocked = x.UrlLocked,
                                            DateUnlocked = x.DateUnlocked,
                                            Percent = x.Percent,
                                            GamerScore = x.GamerScore
                                        }).ToList();

                                        gameAchievements.Items = AllAchievements;
                                        gameAchievements.SourcesLink = new SourceLink { GameName = exMatch.Name, Name = "Exophase", Url = exUrl };
                                    }
                                }
                            }
                            else
                            {
                                if (!GogApi.IsUserLoggedIn)
                                {
                                    ShowNotificationPluginNoAuthenticate(ExternalPlugin.SuccessStory);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false, "Error while using Exophase fallback for GOG achievements", true, PluginDatabase.PluginName);
                        }

                        // Final Fallback: Try TrueAchievements (Xbox/Steam origin) if Exophase yields no results or fails
                        if (!gameAchievements.HasData)
                        {
                            try
                            {
                                Logger.Info($"GOG.GetAchievements: trying TrueAchievements fallback for {game.Name}");
                                var taSearch = TrueAchievements.SearchGame(game, TrueAchievements.OriginData.Xbox);
                                if (taSearch == null || taSearch.Count == 0)
                                {
                                    taSearch = TrueAchievements.SearchGame(game, TrueAchievements.OriginData.Steam);
                                }

                                if (taSearch?.Count > 0)
                                {
                                    var scored = taSearch.Select(x => new { Item = x, Score = Fuzz.TokenSetRatio(game.Name.ToLower(), x.GameName.ToLower()) })
                                        .OrderByDescending(x => x.Score)
                                        .FirstOrDefault();

                                    if (scored != null && scored.Score >= 80)
                                    {
                                        var bestMatch = scored.Item;
                                        var images = TrueAchievements.GetDataImages(bestMatch.GameUrl);
                                        if (images?.Count > 0)
                                        {
                                            AllAchievements = images.Select(x => new Achievement
                                            {
                                                ApiName = x.Key,
                                                Name = x.Key,
                                                UrlUnlocked = x.Value,
                                                UrlLocked = x.Value,
                                                Percent = 0
                                            }).ToList();

                                            gameAchievements.Items = AllAchievements;
                                            gameAchievements.SourcesLink = new SourceLink { GameName = bestMatch.GameName, Name = "TrueAchievements", Url = bestMatch.GameUrl };
                                            Logger.Info($"GOG.GetAchievements: found {AllAchievements.Count} achievements on TrueAchievements for {game.Name}");
                                        }
                                    }
                                }
                            }
                            catch (Exception exTa)
                            {
                                Common.LogError(exTa, false, "Error while using TrueAchievements fallback for GOG achievements", true, PluginDatabase.PluginName);
                            }
                        }
                    }

                    // Set source link
                    if (gameAchievements.HasAchievements)
                    {
                        gameAchievements.SourcesLink = GogApi.GetAchievementsSourceLink(game.Name, game.GameId, GogApi.CurrentAccountInfos);
                    }
                }
                catch (Exception ex)
                {
                    ShowNotificationPluginError(ex);
                    return gameAchievements;
                }
            }
            else
            {
                ShowNotificationPluginNoAuthenticate(ExternalPlugin.SuccessStory);
            }

            gameAchievements.SetRaretyIndicator();
            PluginDatabase.AddOrUpdate(gameAchievements);
            return gameAchievements;
        }


        #region Configuration
        public override bool ValidateConfiguration()
        {
            if (!PluginDatabase.PluginSettings.Settings.PluginState.GogIsEnabled)
            {
                ShowNotificationPluginDisable(ResourceProvider.GetString("LOCSuccessStoryNotificationsGogDisabled"));
                return false;
            }
            else
            {
                if (CachedConfigurationValidationResult == null)
                {
                    CachedConfigurationValidationResult = IsConnected();

                    if (!(bool)CachedConfigurationValidationResult)
                    {
                        ShowNotificationPluginNoAuthenticate(ExternalPlugin.SuccessStory);
                    }
                    else
                    {
                        CachedConfigurationValidationResult = IsConfigured();

                        if (!(bool)CachedConfigurationValidationResult)
                        {
                            ShowNotificationPluginNoConfiguration();
                        }
                    }
                }
                else if (!(bool)CachedConfigurationValidationResult)
                {
                    ShowNotificationPluginErrorMessage(ExternalPlugin.SuccessStory);
                }

                return (bool)CachedConfigurationValidationResult;
            }
        }

        public override bool IsConnected()
        {
            if (CachedIsConnectedResult == null)
            {
                CachedIsConnectedResult = GogApi.IsUserLoggedIn;
            }

            return (bool)CachedIsConnectedResult;
        }

        public override bool IsConfigured()
        {
            return IsConnected();
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableGog;
        }

        public override void ResetCachedConfigurationValidationResult()
        {
            CachedConfigurationValidationResult = null;
            GogApi.ResetIsUserLoggedIn();
        }

        public override void ResetCachedIsConnectedResult()
        {
            CachedIsConnectedResult = null;
            GogApi.ResetIsUserLoggedIn();
        }
        #endregion
    }
}
