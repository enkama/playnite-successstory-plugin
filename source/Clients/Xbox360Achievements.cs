using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Data;
using CommonPluginsShared;
using SuccessStory.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CommonPluginsShared.Extensions;
using CommonPlayniteShared.Common;
using SteamKit2.GC.TF2.Internal;
using CommonPluginsShared.Models;
using System.Threading.Tasks;
using System.Windows;

namespace SuccessStory.Clients
{
    public class Xbox360Achievements : GenericAchievements
    {
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private string _xeniaPath;
        private bool _isInitialized;

        // Path constants
        private const string SUCCESS_STORY_GUID = "cebe6d32-8c46-4459-b993-5a5189d60788";
        private readonly string _playniteAppData;

        // The four main paths we'll use
        private string _xeniaLogFilePath;
        private string _xeniaAchievementsDir;
        private string _successStoryDataDir;
        private string _xeniaJsonTempDir;

        public Xbox360Achievements() : base("Xbox360")
        {
            _playniteApi = API.Instance;
            _logger = LogManager.GetLogger();
            _isInitialized = false;
            _xeniaPath = PluginDatabase.PluginSettings.Settings.XeniaInstallationFolder;
            _playniteAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            InitializePaths();
            _logger.Info($"Xbox360Achievements initialized with Xenia path: {_xeniaPath}");
        }

        public Xbox360Achievements(IPlayniteAPI playniteApi, string xeniaPath) : base("Xbox360")
        {
            _playniteApi = playniteApi;
            _xeniaPath = xeniaPath;
            _logger = LogManager.GetLogger();
            _isInitialized = false;
            _playniteAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            InitializePaths();
        }

        private void InitializePaths()
        {
            if (string.IsNullOrEmpty(_xeniaPath))
            {
                _logger.Error("Xbox360: Xenia path is null or empty");
                return;
            }

            string xeniaDir = Path.GetDirectoryName(_xeniaPath);
            _xeniaLogFilePath = Path.Combine(xeniaDir, "xenia.log");
            _xeniaAchievementsDir = Path.Combine(xeniaDir, "Achievements".TrimEnd('\\') + '\\');
            _successStoryDataDir = Directory.Exists(Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory"))
                ? Path.Combine(_playniteAppData, "Playnite", "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory".TrimEnd('\\') + '\\')
                : Path.Combine(_playniteApi.Paths.ApplicationPath, "ExtensionsData", SUCCESS_STORY_GUID, "SuccessStory".TrimEnd('\\') + '\\');
            _xeniaJsonTempDir = _xeniaAchievementsDir;
        }

        public override GameAchievements GetAchievements(Game game)
        {
            _logger.Info($"Xbox360: Getting achievements for game: {game.Name}");
            GameAchievements gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
            bool saved = false;

            if (!EnabledInSettings())
            {
                _logger.Info("Xbox360: Achievement tracking is disabled in settings");
                return gameAchievements;
            }

            if (!_isInitialized && !string.IsNullOrEmpty(_xeniaPath))
            {
                try
                {
                    InitializeXeniaEnvironment(_xeniaPath);
                    _isInitialized = true;

                    if (IsConfigured())
                    {
                        string gameId = game.Id.ToString();
                        if (!string.IsNullOrEmpty(gameId))
                        {
                            string successStoryJsonFile = Path.Combine(_successStoryDataDir, $"{gameId}.json");
                            string tempJsonFile = Path.Combine(_xeniaJsonTempDir, $"{gameId}.json");
                            string achievementTextFile = Path.Combine(_xeniaAchievementsDir, $"{gameId}.txt");

                            saved = ProcessAchievementsImproved(gameId, successStoryJsonFile, tempJsonFile, achievementTextFile, game);

                            var oldData = SuccessStory.PluginDatabase.Get(game.Id, true);
                            gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
                            gameAchievements.CopyDiffTo(oldData);

                            SuccessStory.PluginDatabase.Database.OnItemUpdated(
                                new List<ItemUpdateEvent<GameAchievements>>() {
                                    new ItemUpdateEvent<GameAchievements>(oldData, gameAchievements)
                                });

                            if (API.Instance.MainView.SelectedGames?.FirstOrDefault()?.Id == game.Id)
                            {
                                API.Instance.MainView.SelectGames(new List<Guid> { game.Id });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Xbox360: Error processing achievements: {ex.Message}");
                    Common.LogError(ex, false, true, PluginDatabase.PluginName);
                }
            }
            else if (!IsConfigured())
            {
                ShowNotificationPluginNoConfiguration();
                return gameAchievements;
            }

            if (!saved)
            {
                gameAchievements.SetRaretyIndicator();
                PluginDatabase.AddOrUpdate(gameAchievements);
            }
            return gameAchievements;
        }

        private bool ProcessAchievementsImproved(string gameId, string successStoryJsonFile, string tempJsonFile, string achievementTextFile, Game game)
        {
            var achievements = new List<Achievement>();

            try
            {
                if (File.Exists(successStoryJsonFile))
                {
                    achievements = ParseExistingAchievements(successStoryJsonFile);
                }

                var unlockedAchievements = GetUnlockedAchievements(achievementTextFile);
                UpdateAchievementUnlockDates(achievements, unlockedAchievements);
                SaveAchievementsAtomically(achievements, successStoryJsonFile, game);

                _logger.Info($"Xbox360: Processed {unlockedAchievements.Count} unlocked achievements");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to process achievements: {ex.Message}");
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
                return false;
            }
        }

        private Dictionary<string, DateTime> GetUnlockedAchievements(string achievementTextFile)
        {
            var unlockedAchievements = new Dictionary<string, DateTime>();

            if (File.Exists(_xeniaLogFilePath))
            {
                DateTime logTimestamp = File.GetCreationTime(_xeniaLogFilePath);

                foreach (var line in File.ReadAllLines(_xeniaLogFilePath))
                {
                    if (line.Contains("Achievement unlocked:"))
                    {
                        var achievementName = line.Split(new[] { "Achievement unlocked:" }, StringSplitOptions.None)[1]
                            .Replace("\r", "")
                            .Replace("i> ", "")
                            .Trim();

                        achievementName = Regex.Replace(achievementName, @"i>\s+[A-F0-9]{8}", "");

                        if (!string.IsNullOrEmpty(achievementName))
                        {
                            unlockedAchievements[achievementName] = logTimestamp;

                            if (!File.Exists(achievementTextFile))
                            {
                                string entry = FormatAchievement(achievementName, logTimestamp);
                                File.WriteAllText(achievementTextFile, entry);
                            }
                            else if (!File.ReadAllText(achievementTextFile).Contains($"\"{achievementName}\""))
                            {
                                string entry = FormatAchievement(achievementName, logTimestamp);
                                File.AppendAllText(achievementTextFile, Environment.NewLine + entry);
                            }
                        }
                    }
                }
            }

            if (File.Exists(achievementTextFile))
            {
                var lines = File.ReadAllLines(achievementTextFile);
                for (int i = 0; i < lines.Length; i += 2)
                {
                    if (i + 1 < lines.Length && lines[i].StartsWith("Unlocked:"))
                    {
                        var match = Regex.Match(lines[i + 1].Trim(), @"""([^""]+)""\s+""([^""]+)""");
                        if (match.Success && DateTime.TryParse(match.Groups[2].Value, out DateTime unlockDate))
                        {
                            string name = match.Groups[1].Value;
                            if (!unlockedAchievements.ContainsKey(name))
                            {
                                unlockedAchievements[name] = unlockDate;
                            }
                        }
                    }
                }
            }

            return unlockedAchievements;
        }

        private string FormatAchievement(string name, DateTime date)
        {
            return $"Unlocked:\r\n\"{name}\" \"{date.ToString("yyyy-MM-ddTHH:mm:ss")}\"";
        }

        private List<Achievement> ParseExistingAchievements(string jsonPath)
        {
            try
            {
                if (File.Exists(jsonPath))
                {
                    string jsonContent = File.ReadAllText(jsonPath);
                    var gameAchievements = Serialization.FromJson<GameAchievements>(jsonContent);
                    return gameAchievements.Items ?? new List<Achievement>();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to parse existing achievements: {ex.Message}");
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
            }
            return new List<Achievement>();
        }

        private void UpdateAchievementUnlockDates(List<Achievement> achievements, Dictionary<string, DateTime> unlockedAchievements)
        {
            foreach (var achievement in achievements)
            {
                if (unlockedAchievements.ContainsKey(achievement.Name))
                {
                    achievement.DateUnlocked = unlockedAchievements[achievement.Name];
                }
            }

            achievements.Sort((a, b) => Nullable.Compare(a.DateUnlocked, b.DateUnlocked));
        }

        private void SaveAchievementsAtomically(List<Achievement> achievements, string finalPath, Game game)
        {
            string gameId = game.Id.ToString();
            string tempPath = Path.Combine(_xeniaAchievementsDir, $"{gameId}.temp.json");

            try
            {
                GameAchievements gameAchievements;
                if (File.Exists(finalPath))
                {
                    string existingJson = File.ReadAllText(finalPath);
                    gameAchievements = Serialization.FromJson<GameAchievements>(existingJson);

                    foreach (var newAchievement in achievements.Where(a => a.DateUnlocked.HasValue))
                    {
                        var existingAchievement = gameAchievements.Items.FirstOrDefault(x => x.Name == newAchievement.Name);
                        if (existingAchievement != null)
                        {
                            DateTime formattedDate = newAchievement.DateUnlocked.Value;
                            existingAchievement.DateUnlocked = DateTime.ParseExact(
                                formattedDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                                "yyyy-MM-ddTHH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture
                            );
                        }
                    }
                }
                else
                {
                    gameAchievements = SuccessStory.PluginDatabase.GetDefault(game);
                    gameAchievements.Items = achievements;
                }

                gameAchievements.DateLastRefresh = DateTime.UtcNow;
                gameAchievements.Name = game.Name;

                string json = Serialization.ToJson(gameAchievements);
                File.WriteAllText(tempPath, json);

                string verificationJson = File.ReadAllText(tempPath);
                var verificationAchievements = Serialization.FromJson<GameAchievements>(verificationJson);

                if (verificationAchievements != null)
                {
                    if (File.Exists(finalPath))
                    {
                        File.Delete(finalPath);
                    }
                    File.Copy(tempPath, finalPath);
                    gameAchievements.IsManual = true;
                    gameAchievements.SetRaretyIndicator();
                    SuccessStory.PluginDatabase.AddOrUpdate(gameAchievements);
                    SuccessStory.PluginDatabase.SetThemesResources(game);
                }
                else
                {
                    throw new Exception("Achievement data verification failed");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to save achievements: {ex.Message}");
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
                throw;
            }
        }

        public void InitializeXeniaEnvironment(string xeniaPath)
        {
            try
            {
                _xeniaPath = xeniaPath;
                InitializePaths();

                if (!Directory.Exists(_xeniaAchievementsDir))
                {
                    Directory.CreateDirectory(_xeniaAchievementsDir);
                }

                string configPath = Path.Combine(Path.GetDirectoryName(_xeniaPath), "xenia-canary.config.toml");
                UpdateXeniaConfig(configPath);
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to initialize environment: {ex.Message}");
                throw;
            }
        }

        private void UpdateXeniaConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    File.WriteAllText(configPath, "show_achievement_notification = true\n");
                    return;
                }

                var configLines = File.ReadAllLines(configPath).ToList();
                bool hasAchievementSetting = configLines.Any(l => l.TrimStart().StartsWith("show_achievement_notification"));

                if (!hasAchievementSetting)
                {
                    configLines.Add("show_achievement_notification = true");
                    File.WriteAllLines(configPath, configLines);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Xbox360: Failed to update Xenia config: {ex.Message}");
                Common.LogError(ex, false, true, PluginDatabase.PluginName);
                throw;
            }
        }

        public override bool ValidateConfiguration()
        {
            if (CachedConfigurationValidationResult == null)
            {
                CachedConfigurationValidationResult = IsConfigured();

                if (!(bool)CachedConfigurationValidationResult)
                {
                    ShowNotificationPluginNoConfiguration();
                }
            }
            else if (!(bool)CachedConfigurationValidationResult)
            {
                ShowNotificationPluginErrorMessage(PlayniteTools.ExternalPlugin.SuccessStory);
            }

            return (bool)CachedConfigurationValidationResult;
        }

        public override bool IsConfigured()
        {
            if (string.IsNullOrEmpty(_xeniaPath))
            {
                return false;
            }

            bool hasAchievementsDir = Directory.Exists(_xeniaAchievementsDir);
            bool hasSuccessStoryDir = Directory.Exists(_successStoryDataDir);
            bool hasConfigFile = File.Exists(Path.Combine(Path.GetDirectoryName(_xeniaPath), "xenia-canary.config.toml"));

            return hasAchievementsDir && hasSuccessStoryDir && hasConfigFile;
        }

        public override bool EnabledInSettings()
        {
            return PluginDatabase.PluginSettings.Settings.EnableXbox360Achievements;
        }
    }
}
