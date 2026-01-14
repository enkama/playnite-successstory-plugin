using CommonPluginsShared;
using CommonPluginsShared.Extensions;
using CommonPluginsStores.Epic;
using CommonPluginsStores.Models;
using Playnite.SDK;
using Playnite.SDK.Models;
using SuccessStory.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using static CommonPluginsShared.PlayniteTools;
using FuzzySharp;
using CommonPluginsShared.Models;

namespace SuccessStory.Clients
{
    public class EpicAchievements : GenericAchievements
    {
        private EpicApi EpicApi => SuccessStory.EpicApi;


        public EpicAchievements() : base("Epic", CodeLang.GetEpicLang(API.Instance.ApplicationSettings.Language), CodeLang.GetGogLang(API.Instance.ApplicationSettings.Language))
        {

        }

        public override GameAchievements GetAchievements(Game game)
        {
            var swOverall = Stopwatch.StartNew();
            Logger.Info($"Epic.GetAchievements START - {game.Name}");
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            List<Achievement> AllAchievements = new List<Achievement>();

            if (IsConnected())
            {
                try
                {
                    var assets = EpicApi.GetAssets();

                    var asset = assets.FirstOrDefault(x => x.AppName.IsEqual(game.GameId));
                    string targetNamespace = asset?.Namespace ?? game.GameId;

                    if (asset == null)
                    {
                        Logger.Warn($"No asset for the Epic game {game.Name}. Using GameId {game.GameId} as namespace.");
                    }

                    var epicAchievements = EpicApi.GetAchievements(targetNamespace, EpicApi.CurrentAccountInfos);
                    if (epicAchievements?.Count > 0)
                    {
                        AllAchievements = epicAchievements.Select(x => new Achievement
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
                    else
                    {
                        // No achievements returned by Epic API — try Exophase as a fallback to obtain achievement metadata/images
                        try
                        {
                            if (SuccessStory.ExophaseAchievements != null)
                            {
                                Logger.Info($"Epic.GetAchievements: no achievements from Epic API for {game.Name}, trying Exophase fallback");
                                var exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name, "Epic");
                                if (exSearch == null || exSearch.Count == 0)
                                {
                                    exSearch = SuccessStory.ExophaseAchievements.SearchGame(game.Name);
                                }

                                SearchResult exMatch = null;
                                if (exSearch?.Count > 0)
                                {
                                    string normalizedGame = NormalizeGameName(game.Name);

                                    var platformPreferred = exSearch.Where(x => x.Platforms != null && x.Platforms.Any(p => p.Equals("Epic", StringComparison.InvariantCultureIgnoreCase))).ToList();
                                    var candidates = platformPreferred.Count > 0 ? platformPreferred : exSearch;

                                    // If no explicit Epic platform entries exist, prefer candidates whose URL indicates PC/Epic (avoid console-specific variants like "-nintendo-")
                                    if (platformPreferred.Count == 0 && candidates.Count > 1)
                                    {
                                        var preferredUrlTokens = new[] { "epic", "pc", "windows", "steam", "gog", "ubisoft", "uplay" };
                                        var urlFiltered = candidates.Where(x => !string.IsNullOrEmpty(x.Url) && preferredUrlTokens.Any(t => x.Url.IndexOf(t, StringComparison.InvariantCultureIgnoreCase) >= 0)).ToList();
                                        if (urlFiltered.Count > 0)
                                        {
                                            candidates = urlFiltered;
                                        }
                                    }

                                    var scored = candidates.Select(x => new { Item = x, Score = Fuzz.TokenSetRatio(normalizedGame, NormalizeGameName(x.Name)) })
                                        .OrderByDescending(x => x.Score)
                                        .ToList();

                                    // Choose best candidate if score passes threshold, otherwise try exact normalized match or fallback to first
                                    int threshold = 75;
                                    if (scored.First().Score >= threshold)
                                    {
                                        exMatch = scored.First().Item;
                                    }
                                    else
                                    {
                                        // try exact normalized name match
                                        exMatch = exSearch.FirstOrDefault(x => NormalizeGameName(x.Name).IsEqual(normalizedGame));
                                        if (exMatch == null)
                                        {
                                            // fallback to best even if under threshold but log warning
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
                                        // Map Exophase items into achievements list
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
                                        // Set source link to Exophase so users can see origin
                                        gameAchievements.SourcesLink = new SourceLink { GameName = exMatch.Name, Name = "Exophase", Url = exUrl };
                                    }
                                }
                            }
                            else
                            {
                                if (!EpicApi.IsUserLoggedIn)
                                {
                                    ShowNotificationPluginNoAuthenticate(ExternalPlugin.SuccessStory);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Common.LogError(ex, false, "Error while using Exophase fallback for Epic achievements", true, PluginDatabase.PluginName);
                        }

                        // 3) Final Fallback: Try TrueAchievements (Xbox/Steam origin) if Exophase yields no results or fails
                        if (!gameAchievements.HasData)
                        {
                            try
                            {
                                Logger.Info($"Epic.GetAchievements: trying TrueAchievements fallback for {game.Name}");
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
                                            Logger.Info($"Epic.GetAchievements: found {AllAchievements.Count} achievements on TrueAchievements for {game.Name}");
                                        }
                                    }
                                }
                            }
                            catch (Exception exTa)
                            {
                                Common.LogError(exTa, false, "Error while using TrueAchievements fallback for Epic achievements", true, PluginDatabase.PluginName);
                            }
                        }
                    }

                    // Set source link
                    if (gameAchievements.HasAchievements && gameAchievements.SourcesLink == null)
                    {
                        var swSlug = Stopwatch.StartNew();
                        string productSlug = EpicApi.GetProductSlug(targetNamespace);
                        swSlug.Stop();
                        gameAchievements.SourcesLink = EpicApi.GetAchievementsSourceLink(game.Name, productSlug, EpicApi.CurrentAccountInfos);
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

            swOverall.Stop();
            Logger.Info($"Epic.GetAchievements STOP - {game.Name} - {swOverall.ElapsedMilliseconds}ms");
            return gameAchievements;
        }


        #region Configuration

        public override bool ValidateConfiguration()
        {
            if (!PluginDatabase.PluginSettings.Settings.PluginState.EpicIsEnabled)
            {
                ShowNotificationPluginDisable(ResourceProvider.GetString("LOCSuccessStoryNotificationsEpicDisabled"));
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
                CachedIsConnectedResult = EpicApi.IsUserLoggedIn;
            }

            return (bool)CachedIsConnectedResult;
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableEpic;
        }

        public override void ResetCachedConfigurationValidationResult()
        {
            CachedConfigurationValidationResult = null;
            EpicApi.ResetIsUserLoggedIn();
        }

        public override void ResetCachedIsConnectedResult()
        {
            CachedIsConnectedResult = null;
            EpicApi.ResetIsUserLoggedIn();
        }

        #endregion
    }
}