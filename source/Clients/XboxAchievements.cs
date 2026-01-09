using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using CommonPluginsShared;
using CommonPlayniteShared.PluginLibrary.XboxLibrary.Models;
using SuccessStory.Models;
using CommonPluginsShared.Models;
using CommonPluginsShared.Extensions;
using static CommonPluginsShared.PlayniteTools;
using CommonPlayniteShared.PluginLibrary.XboxLibrary.Services;
using SuccessStory.Models.Xbox;

namespace SuccessStory.Clients
{
    public class XboxAchievements : GenericAchievements
    {
        protected static readonly Lazy<XboxAccountClient> xboxAccountClient = new Lazy<XboxAccountClient>(() => new XboxAccountClient(API.Instance, PluginDatabase.Paths.PluginUserDataPath + "\\..\\" + PlayniteTools.GetPluginId(ExternalPlugin.XboxLibrary)));
        internal static XboxAccountClient XboxAccountClient => xboxAccountClient.Value;

        private static string AchievementsBaseUrl => @"https://achievements.xboxlive.com/users/xuid({0})/achievements";
        private static string TitleAchievementsBaseUrl => @"https://achievements.xboxlive.com/users/xuid({0})/titleachievements";


        public XboxAchievements() : base("Xbox", CodeLang.GetXboxLang(API.Instance.ApplicationSettings.Language))
        {

        }


        public override GameAchievements GetAchievements(Game game)
        {
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            List<Achievement> AllAchievements = new List<Achievement>();

            if (IsConnected())
            {
                try
                {
                    AuthorizationData authData = XboxAccountClient.GetSavedXstsTokens();
                    if (authData == null)
                    {
                        ShowNotificationPluginNoAuthenticate(ExternalPlugin.XboxLibrary);
                        return gameAchievements;
                    }

                    // Run in background with timeout to avoid blocking UI thread and potential deadlocks indefinitely
                    using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        try
                        {
                            AllAchievements = Task.Run(async () => await GetXboxAchievements(game, authData), cts.Token).GetAwaiter().GetResult();
                        }
                        catch (Exception ex) when (ex is OperationCanceledException || ex is TaskCanceledException)
                        {
                            Logger.Warn($"Xbox achievements retrieval timed out for {game.Name} after 30 seconds.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, $"Error on GetXboxAchievements() for {game.Name}");
                            ShowNotificationPluginError(ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowNotificationPluginError(ex);
                }
            }
            else
            {
                ShowNotificationPluginNoAuthenticate(ExternalPlugin.XboxLibrary);
            }


            gameAchievements.Items = AllAchievements;


            // Set source link
            if (gameAchievements.HasAchievements)
            {
                gameAchievements.SourcesLink = new SourceLink
                {
                    GameName = game.Name,
                    Name = "Xbox",
                    Url = $"https://account.xbox.com/{LocalLang}/GameInfoHub?titleid={GetTitleId(game)}&selectedTab=achievementsTab&activetab=main:mainTab2"
                };
            }

            // Set rarity from Exophase — guarded to avoid crashing when Exophase integration is broken
            if (gameAchievements.HasAchievements)
            {
                try
                {
                    if (SuccessStory.ExophaseAchievements != null && SuccessStory.ExophaseAchievements.IsConnected())
                    {
                        SuccessStory.ExophaseAchievements.SetRarety(gameAchievements, Services.SuccessStoryDatabase.AchievementSource.Xbox);
                    }
                    else
                    {
                        Logger.Warn("Exophase not connected or unavailable - skipping rarity fetch for Xbox achievements.");
                    }
                }
                catch (Exception ex)
                {
                    // Log and continue — do not let Exophase failures crash Xbox achievement retrieval
                    Common.LogError(ex, false, true, PluginDatabase.PluginName);
                }
            }

            gameAchievements.SetRaretyIndicator();
            PluginDatabase.AddOrUpdate(gameAchievements);
            return gameAchievements;
        }

        #region Configuration

        public override bool ValidateConfiguration()
        {
            if (!PluginDatabase.PluginSettings.Settings.PluginState.XboxIsEnabled)
            {
                ShowNotificationPluginDisable(ResourceProvider.GetString("LOCSuccessStoryNotificationsXboxDisabled"));
                return false;
            }
            else
            {
                if (CachedConfigurationValidationResult == null)
                {
                    CachedConfigurationValidationResult = IsConnected();

                    if (!(bool)CachedConfigurationValidationResult)
                    {
                        ShowNotificationPluginNoAuthenticate(ExternalPlugin.XboxLibrary);
                    }
                }
                else if (!(bool)CachedConfigurationValidationResult)
                {
                    ShowNotificationPluginErrorMessage(ExternalPlugin.XboxLibrary);
                }

                return (bool)CachedConfigurationValidationResult;
            }
        }


        public override bool IsConnected()
        {
            if (CachedIsConnectedResult == null)
            {
                // Sync-over-async: We run the full async flow on a background thread to avoid deadlocks.
                // Note: Calling .GetAwaiter().GetResult() from a thread with a synchronization context (like the UI thread)
                // still carries some risk. Prefer using IsConnectedAsync() whenever possible.
                CachedIsConnectedResult = Task.Run(async () => await IsConnectedAsync()).GetAwaiter().GetResult();
            }

            return (bool)CachedIsConnectedResult;
        }

        public override async Task<bool> IsConnectedAsync()
        {
            if (CachedIsConnectedResult == null)
            {
                bool loggedIn = await XboxAccountClient.GetIsUserLoggedIn();
                if (!loggedIn && File.Exists(XboxAccountClient.liveTokensPath))
                {
                    await XboxAccountClient.RefreshTokens();
                    loggedIn = await XboxAccountClient.GetIsUserLoggedIn();
                }
                CachedIsConnectedResult = loggedIn;
            }

            return (bool)CachedIsConnectedResult;
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableXbox;
        }

        #endregion

        #region Xbox

        private string GetTitleId(Game game)
        {
            string titleId = string.Empty;
            if (game.GameId?.StartsWith("CONSOLE_") == true)
            {
                string[] consoleGameIdParts = game.GameId.Split('_');
                titleId = consoleGameIdParts[1];

                Common.LogDebug(true, $"{ClientName} - name: {game.Name} - gameId: {game.GameId} - titleId: {titleId}");
            }
            else if (!game.GameId.IsNullOrEmpty())
            {
                TitleHistoryResponse.Title libTitle = Task.Run(async () => await XboxAccountClient.GetTitleInfo(game.GameId)).GetAwaiter().GetResult();
                if (libTitle != null)
                {
                    titleId = libTitle.titleId;
                }
                else
                {
                    Logger.Warn($"{ClientName} - No title info found for {game.GameId}");
                }

                Common.LogDebug(true, $"{ClientName} - name: {game.Name} - gameId: {game.GameId} - titleId: {titleId}");
            }
            return titleId;
        }

        private async Task<TContent> GetSerializedContentFromUrl<TContent>(string url, AuthorizationData authData, string contractVersion) where TContent : class
        {
            Common.LogDebug(true, $"{ClientName} - url: {url}");

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", Web.UserAgent);
                SetAuthenticationHeaders(client.DefaultRequestHeaders, authData, contractVersion);

                HttpResponseMessage response = await client.GetAsync(url);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        Logger.Warn($"{ClientName} - User is not authenticated - {response.StatusCode}");
                        API.Instance.Notifications.Add(new NotificationMessage(
                            $"{PluginDatabase.PluginName}-Xbox-notAuthenticate",
                            $"{PluginDatabase.PluginName}\r\n{ResourceProvider.GetString("LOCSuccessStoryNotificationsXboxNotAuthenticate")}",
                            NotificationType.Error
                        ));
                    }
                    else
                    {
                        Logger.Warn($"{ClientName} - Error on GetXboxAchievements() - {response.StatusCode}");
                        API.Instance.Notifications.Add(new NotificationMessage(
                            $"{PluginDatabase.PluginName}-Xbox-webError",
                            $"{PluginDatabase.PluginName}\r\nXbox achievements: {ResourceProvider.GetString("LOCImportError")}",
                            NotificationType.Error
                        ));
                    }

                    return null;
                }

                string cont = await response.Content.ReadAsStringAsync();
                Common.LogDebug(true, cont);

                return Serialization.FromJson<TContent>(cont);
            }
        }


        private async Task<List<Achievement>> GetXboxAchievements(Game game, AuthorizationData authorizationData)
        {
            var getAchievementMethods = new List<Func<Game, AuthorizationData, Task<List<Achievement>>>>
            {
                GetXboxOneAchievements
            };

            // Only add Xbox360 retrieval when enabled in settings to avoid unnecessary Xenia lookups/crashes
            try
            {
                if (PluginDatabase?.PluginSettings?.Settings?.EnableXbox360Achievements == true)
                {
                    getAchievementMethods.Add(GetXbox360Achievements);
                }
            }
            catch (Exception ex)
            {
                // Defensive: log and proceed with Xbox One method only
                Logger.Warn($"XboxAchievements: failed to evaluate Xbox360 setting - {ex.Message}");
            }

            if (game.Platforms != null && game.Platforms.Any(p => p.SpecificationId == "xbox360") && getAchievementMethods.Contains(GetXbox360Achievements))
            {
                getAchievementMethods.Reverse();
            }

            foreach (Func<Game, AuthorizationData, Task<List<Achievement>>> getAchievementsMethod in getAchievementMethods)
            {
                try
                {
                    List<Achievement> result = await getAchievementsMethod.Invoke(game, authorizationData);
                    if (result != null && result.Any())
                    {
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("User is not authenticated", StringComparison.InvariantCultureIgnoreCase))
                    {
                        ShowNotificationPluginNoAuthenticate(ExternalPlugin.XboxLibrary);
                    }
                    else
                    {
                        Common.LogError(ex, false, true, PluginDatabase.PluginName);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets achievements for games that have come out on or since the Xbox One. This includes recent PC releases and Xbox Series X/S games.
        /// </summary>
        /// <param name="game"></param>
        /// <param name="authorizationData"></param>
        /// <returns></returns>
        private async Task<List<Achievement>> GetXboxOneAchievements(Game game, AuthorizationData authorizationData)
        {
            if (authorizationData is null)
            {
                throw new ArgumentNullException(nameof(authorizationData));
            }

            string xuid = authorizationData.DisplayClaims.xui[0].xid;

            Common.LogDebug(true, $"GetXboxAchievements() - name: {game.Name} - gameId: {game.GameId}");
            
            string titleId = GetTitleId(game);

            string url = string.Format(AchievementsBaseUrl, xuid) + $"?titleId={titleId}&maxItems=1000";
            if (titleId.IsNullOrEmpty())
            {
                url = string.Format(AchievementsBaseUrl, xuid) + "?maxItems=10000";
                Logger.Warn($"{ClientName} - Bad request");
            }

            XboxOneAchievementResponse response = await GetSerializedContentFromUrl<XboxOneAchievementResponse>(url, authorizationData, "2");

            List<XboxOneAchievement> relevantAchievements;
            if (titleId.IsNullOrEmpty())
            {
                relevantAchievements = response.achievements.Where(x => x.titleAssociations.First().name.IsEqual(game.Name, true)).ToList();
                Common.LogDebug(true, $"Not found with {game.GameId} for {game.Name} - {relevantAchievements.Count}");
            }
            else
            {
                relevantAchievements = response.achievements;
                Common.LogDebug(true, $"Find with {titleId} & {game.GameId} for {game.Name} - {relevantAchievements.Count}");
            }

            List<Achievement> achievements = relevantAchievements.Select(ConvertToAchievement).ToList();
            return achievements;
        }

        /// <summary>
        /// Gets achievements for Xbox 360 and Games for Windows Live
        /// </summary>
        /// <param name="game"></param>
        /// <param name="authorizationData"></param>
        /// <returns></returns>
        private async Task<List<Achievement>> GetXbox360Achievements(Game game, AuthorizationData authorizationData)
        {
            if (authorizationData is null)
            {
                throw new ArgumentNullException(nameof(authorizationData));
            }

            string xuid = authorizationData.DisplayClaims.xui[0].xid;

            Common.LogDebug(true, $"GetXbox360Achievements() - name: {game.Name} - gameId: {game.GameId}");

            string titleId = GetTitleId(game);

            if (titleId.IsNullOrEmpty())
            {
                Common.LogDebug(true, $"Couldn't find title ID for game name: {game.Name} - gameId: {game.GameId}");
                return new List<Achievement>();
            }

            // gets the player-unlocked achievements
            string unlockedAchievementsUrl = string.Format(AchievementsBaseUrl, xuid) + $"?titleId={titleId}&maxItems=1000";
            Task<Xbox360AchievementResponse> getUnlockedAchievementsTask = GetSerializedContentFromUrl<Xbox360AchievementResponse>(unlockedAchievementsUrl, authorizationData, "1");

            // gets all of the game's achievements, but they're all marked as locked
            string allAchievementsUrl = string.Format(TitleAchievementsBaseUrl, xuid) + $"?titleId={titleId}&maxItems=1000";
            Task<Xbox360AchievementResponse> getAllAchievementsTask = GetSerializedContentFromUrl<Xbox360AchievementResponse>(allAchievementsUrl, authorizationData, "1");

            await Task.WhenAll(getUnlockedAchievementsTask, getAllAchievementsTask);

            Dictionary<int, Xbox360Achievement> mergedAchievements = getUnlockedAchievementsTask.Result.achievements.ToDictionary(x => x.id);
            foreach (Xbox360Achievement a in getAllAchievementsTask.Result.achievements)
            {
                if (mergedAchievements.ContainsKey(a.id))
                {
                    continue;
                }

                mergedAchievements.Add(a.id, a);
            }

            List<Achievement> achievements = mergedAchievements.Values.Select(ConvertToAchievement).ToList();

            return achievements;
        }


        private static Achievement ConvertToAchievement(XboxOneAchievement xboxAchievement)
        {
            return new Achievement
            {
                ApiName = string.Empty,
                Name = xboxAchievement.name,
                Description = (xboxAchievement.progression.timeUnlocked == default) ? xboxAchievement.lockedDescription : xboxAchievement.description,
                IsHidden = xboxAchievement.isSecret,
                Percent = 100,
                DateUnlocked = xboxAchievement.progression.timeUnlocked.ToString().Contains(default(DateTime).ToString()) ? (DateTime?)null : xboxAchievement.progression.timeUnlocked,
                UrlLocked = string.Empty,
                UrlUnlocked = xboxAchievement.mediaAssets[0].url,
                GamerScore = float.Parse(xboxAchievement.rewards?.FirstOrDefault(x => x.type.IsEqual("Gamerscore"))?.value ?? "0")
            };
        }

        private static Achievement ConvertToAchievement(Xbox360Achievement xboxAchievement)
        {
            bool unlocked = xboxAchievement.unlocked || xboxAchievement.unlockedOnline;

            return new Achievement
            {
                ApiName = string.Empty,
                Name = xboxAchievement.name,
                Description = unlocked ? xboxAchievement.lockedDescription : xboxAchievement.description,
                IsHidden = xboxAchievement.isSecret,
                Percent = 100,
                DateUnlocked = unlocked ? xboxAchievement.timeUnlocked : (DateTime?)null,
                UrlLocked = string.Empty,
                UrlUnlocked = $"https://image-ssl.xboxlive.com/global/t.{xboxAchievement.titleId:x}/ach/0/{xboxAchievement.imageId:x}",
                GamerScore = xboxAchievement.gamerscore
            };
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="headers"></param>
        /// <param name="auth"></param>
        /// <param name="contractVersion">1 for Xbox 360 era API, 2 for Xbox One or Series X/S</param>
        private void SetAuthenticationHeaders(System.Net.Http.Headers.HttpRequestHeaders headers, AuthorizationData auth, string contractVersion)
        {
            headers.Add("x-xbl-contract-version", contractVersion);
            headers.Add("Authorization", $"XBL3.0 x={auth.DisplayClaims.xui[0].uhs};{auth.Token}");
            headers.Add("Accept-Language", LocalLang);
        }
        
        #endregion
    }
}