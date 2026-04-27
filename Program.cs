using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DotNetEnv;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static Program;

// ============================
// КЛАСС ДЛЯ РАБОТЫ С СООБЩЕНИЯМИ
// ============================

public class MessageManager
{
    private static readonly string _messagesFile = Path.Combine(Paths.Messages, "messages.json");
    private static Dictionary<string, string> _messages;
    private static readonly object _lock = new object();

    public static void LoadMessages()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_messagesFile))
                {
                    var json = File.ReadAllText(_messagesFile);
                    _messages = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>();
                }
                else
                {
                    _messages = new Dictionary<string, string>();
                    Console.WriteLine("⚠️ Файл messages.json не найден. Создайте его!");
                }
            }
            Console.WriteLine($"✅ Загружено {_messages.Count} сообщений");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка загрузки messages.json: {ex.Message}");
            _messages = new Dictionary<string, string>();
        }
    }

    public static string Get(string key, params object[] args)
    {
        if (_messages == null)
            LoadMessages();

        if (_messages.TryGetValue(key, out var message))
        {
            if (args.Length > 0)
            {
                try
                {
                    return string.Format(message, args);
                }
                catch
                {
                    return message;
                }
            }
            return message;
        }

        Console.WriteLine($"⚠️ Сообщение с ключом '{key}' не найдено");
        return $"<b>Ошибка:</b> Сообщение '{key}' не найдено";
    }
}

// ============================
// ОСНОВНОЙ КЛАСС ПРОГРАММЫ
// ============================

public class Program
{
    private static TelegramBotClient? _bot;
    private static ConcurrentDictionary<long, UserData>? _users;
    private static readonly string _messagesFile = Path.Combine(Paths.Messages, "messages.json");
    private static readonly string _usersFile = Path.Combine(Paths.Data, "users.json");
    private static readonly string _configFile = Path.Combine(Paths.Data, "config.json");
    private static readonly string _userCardsFile = Path.Combine(Paths.Data, "usercards.json");
    private static readonly string _userDecksFile = Path.Combine(Paths.Data, "userdecks.json");
    private static readonly string _adminsFile = Path.Combine(Paths.Data, "admins.json");
    private static readonly string _promoCodesFile = Path.Combine(Paths.Data, "promocodes.json");
    private static readonly string _userGemsFile = Path.Combine(Paths.Data, "usergems.json");
    private static readonly string _topicSettingsFile = Path.Combine(Paths.Data, "topic_settings.json");
    private static readonly string _userChestStatsFile = Path.Combine(Paths.Data, "usercheststats.json");
    private static readonly string _evolutionsFile = Path.Combine(Paths.Data, "evolutions.json");
    private static readonly string _userEvolutionsFile = Path.Combine(Paths.Data, "userevolutions.json");
    private static ConcurrentDictionary<string, EvolutionData> _evolutions = new ConcurrentDictionary<string, EvolutionData>();
    private static ConcurrentDictionary<long, UserEvolutionsData> _userEvolutions = new ConcurrentDictionary<long, UserEvolutionsData>();

    private const int CHEST_LIMIT_PER_HOUR = 3;
    private static ConcurrentDictionary<long, UserChestStats> _userChestStats;
    private static ConcurrentDictionary<long, int> _userGems; // ID пользователя -> количество гемов

    public static class Paths
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

        public static string Data => Path.Combine(BaseDir, "Data");
        public static string Messages => Path.Combine(BaseDir, "Messages");
        public static string Images => Path.Combine(BaseDir, "Images");
    }

    public class UserChestStats
    {
        public long UserId { get; set; }
        public List<DateTime> ChestOpens { get; set; } = new List<DateTime>();
        public Dictionary<string, int> ChestCounts { get; set; } = new Dictionary<string, int>()
    {
        { "wooden", 0 },
        { "iron", 0 },
        { "golden", 0 }
    };
        public DateTime ResetTime { get; set; } = DateTime.UtcNow;
    }

    // Цены на ящики в гемах
    private const int WOODEN_CHEST_PRICE = 4;    // 3 гемов (30 золота)
    private const int IRON_CHEST_PRICE = 8;      // 7 гемов (70 золота)
    private const int GOLDEN_CHEST_PRICE = 16;    // 15 гемов (15 золота)

    // Шансы выпадения редкостей для ящиков (в процентах)
    private static readonly Dictionary<string, Dictionary<string, double>> _chestRarityChances = new()
    {
        {
            "wooden", new Dictionary<string, double>
            {
                { "common", 60.0 },
                { "rare", 30.0 },
                { "epic", 7.5 },
                { "legendary", 2.0 },
                { "champion", 0.5 }
            }
        },
        {
            "iron", new Dictionary<string, double>
            {
                { "common", 55.0 },
                { "rare", 30.0 },
                { "epic", 13.0 },
                { "legendary", 4.0 },
                { "champion", 1.0 }
            }
        },
        {
            "golden", new Dictionary<string, double>
            {
                { "common", 46.0 },
                { "rare", 27.5 },
                { "epic", 15.0 },
                { "legendary", 8.0 },
                { "champion", 3.5 }
            }
        }
    };

    // Количество карт в ящиках (от и до)
    private static readonly Dictionary<string, (int min, int max)> _chestCardCount = new()
    {
        { "wooden", (1, 2) },
        { "iron", (2, 3) },
        { "golden", (2, 4) }
    };

    private static ConcurrentDictionary<string, PromoCodeData> _promoCodes = new ConcurrentDictionary<string, PromoCodeData>();
    private static readonly Random _random = new Random();
    private static readonly object _fileLock = new object();

    // Кулдаун 20 минут (1200 секунд)
    private const int COOLDOWN_SECONDS = 1800;
    private const int MAX_FILE_SIZE_MB = 10;
    private const int MAX_MESSAGE_LENGTH = 4096;
    private const int MAX_DECK_SIZE = 8; // Максимальный размер колоды

    private static readonly long _channelId = -1002964101476;
    private static readonly string _channelLink = "https://t.me/WeltobruhChannel";
    private static readonly string _channelUsername = "@WeltobruhChannel";

    // Настройки топиков (загружаются из файла)
    private static Dictionary<long, int> _topicSettings = new Dictionary<long, int>(); // groupId -> topicId

    // Имя бота для команд с упоминанием
    private static string _botUsername = "";

    // Кэш для списков файлов
    private static readonly ConcurrentDictionary<string, string[]> _imageFilesCache = new ConcurrentDictionary<string, string[]>();

    // Система администрирования
    private static long _ownerId = 5072903681; // Замените на ваш Telegram ID
    private static ConcurrentDictionary<long, AdminData> _admins = new ConcurrentDictionary<long, AdminData>();

    // ПОЛНЫЕ ПУТИ К ПАПКАМ
    private static readonly Dictionary<string, string> _imageFolders = new Dictionary<string, string>
{
    { "common", Path.Combine(Paths.Images, "common") },
    { "rare", Path.Combine(Paths.Images, "rare") },
    { "epic", Path.Combine(Paths.Images, "epic") },
    { "legendary", Path.Combine(Paths.Images, "legendary") },
    { "champion", Path.Combine(Paths.Images, "champion") },
    { "evolution", Path.Combine(Paths.Images, "evolution") },
    { "exclusive", Path.Combine(Paths.Images, "exclusive") }
};

    // Редкости карт как в Clash Royale + вес для расчета ранга
    private static readonly Dictionary<string, (int gold, string emoji, string name, int weight)> _rarities = new Dictionary<string, (int, string, string, int)>
{
    { "common", (10, "🔵", "Обычная", 1) },
    { "rare", (25, "🟠", "Редкая", 2) },
    { "epic", (50, "🟣", "Эпическая", 5) },
    { "legendary", (100, "⚪", "Легендарная", 10) },
    { "champion", (200, "🌟", "Чемпионская", 20) },
    { "exclusive", (0, "🔴", "Эксклюзивная", 50) } // Новая редкость
};

    // Для отслеживания карт пользователей
    private static ConcurrentDictionary<long, HashSet<string>> _userCards;

    // Для отслеживания колод пользователей
    private static ConcurrentDictionary<long, UserDeck> _userDecks;

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Основные
    // ============================

    public static async Task Main()
    {
        try
        {
            string envPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../.env");
            if (File.Exists(envPath))
                DotNetEnv.Env.Load(envPath);
            else
                DotNetEnv.Env.Load();
            // Устанавливаем кодировку консоли для поддержки русского языка
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            // Также попробуем установить кодовую страницу 65001 (UTF-8)
            try
            {
                Console.WriteLine("Текущая кодовая страница: " + Console.OutputEncoding.CodePage);
                Console.OutputEncoding = System.Text.Encoding.GetEncoding(65001); // UTF-8
                Console.WriteLine("Новая кодовая страница: " + Console.OutputEncoding.CodePage);
            }
            catch { }

            Console.WriteLine("=== Запуск бота 'Пирожки' ===");

            Console.WriteLine("=== Запуск бота 'Пирожки' ===");

            StartConsoleAutoClearSimple();

            // Настройка логирования
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                LogError($"Критическая ошибка: {e.ExceptionObject}");
            };

            // Загружаем сообщения
            MessageManager.LoadMessages();

            // Загружаем конфигурацию
            var config = LoadConfig();
            if (string.IsNullOrEmpty(config.BotToken) || !config.BotToken.Contains(':'))
            {
                Console.WriteLine("❌ ОШИБКА: Некорректный токен бота!");
                Console.WriteLine("Токен должен быть в формате: '1234567890:ABCdefGhIJKlmNoPQRsTUVwxyZ'");
                return;
            }

            Console.WriteLine("\n=== ПРОВЕРКА ПУТЕЙ К ИЗОБРАЖЕНИЯМ ===");
            CheckImageFolders();

            LoadUsers();
            LoadUserCards();
            LoadUserDecks();
            LoadUserGems();
            LoadUserChestStats();
            LoadAdmins();
            LoadPromoCodes();
            LoadTopicSettings(); // Загружаем настройки топиков
                                 // Загружаем эволюции
            LoadEvolutions();
            LoadUserEvolutions();

            Console.WriteLine($"Загружено пользователей: {_users.Count}");
            Console.WriteLine($"Загружено колод: {_userDecks.Count}");
            Console.WriteLine($"Загружено гемов: {_userGems.Count}");
            Console.WriteLine($"Загружено статистики сундуков: {_userChestStats.Count}");
            Console.WriteLine($"Загружено администраторов: {_admins.Count}");
            Console.WriteLine($"Загружено настроек топиков: {_topicSettings.Count}");
            Console.WriteLine($"Владелец бота: {_ownerId}");

            // Кэшируем списки файлов
            CacheImageFiles();

            _bot = new TelegramBotClient(config.BotToken;

            Console.WriteLine("\n=== ПОДКЛЮЧЕНИЕ К TELEGRAM ===");
            var me = await _bot.GetMe();
            _botUsername = me.Username;
            Console.WriteLine($"✅ Бот подключен: @{_botUsername}");
            Console.WriteLine($"⏰ Кулдаун: {COOLDOWN_SECONDS / 60} минут");
            Console.WriteLine($"📺 ID канала: {_channelId}");
            Console.WriteLine($"🔗 Ссылка на канал: {_channelLink}");
            Console.WriteLine($"🎴 Максимальный размер колоды: {MAX_DECK_SIZE} карт");
            Console.WriteLine($"💎 Магазин ящиков:");
            Console.WriteLine($"   • Деревянный: {WOODEN_CHEST_PRICE} гемов (1-2 карты)");
            Console.WriteLine($"   • Железный: {IRON_CHEST_PRICE} гемов (2-3 карты)");
            Console.WriteLine($"   • Золотой: {GOLDEN_CHEST_PRICE} гемов (2-4 карты)");

            // Создаем резервную копию данных
            CreateBackup();

            using var cts = new CancellationTokenSource();

            _bot.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                cancellationToken: cts.Token
            );

            Console.WriteLine("\n✅ Бот запущен и готов к работе!");
            Console.WriteLine("Нажмите Enter для остановки...\n");

            Console.ReadLine();
            cts.Cancel();

            // Сохраняем данные при выходе
            SaveAllData();
        }
        catch (ApiRequestException ex)
        {
            LogError($"ОШИБКА Telegram API: Код: {ex.ErrorCode}, Сообщение: {ex.Message}");

            if (ex.ErrorCode == 401)
            {
                Console.WriteLine("\n⚠️ Токен неверный или устарел!");
                Console.WriteLine("1. Получите новый токен у @BotFather");
                Console.WriteLine("2. Обновите файл config.json");
                Console.WriteLine("3. Перезапустите бота");
            }
        }
        catch (Exception ex)
        {
            LogError($"Критическая ошибка запуска: {ex}");
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Настройки топиков
    // ============================

    private static void LoadTopicSettings()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_topicSettingsFile))
                {
                    var json = File.ReadAllText(_topicSettingsFile);
                    _topicSettings = JsonConvert.DeserializeObject<Dictionary<long, int>>(json)
                                  ?? new Dictionary<long, int>();
                }
                else
                {
                    _topicSettings = new Dictionary<long, int>();
                }
            }
            LogInfo($"Загружено {_topicSettings.Count} настроек топиков");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки topic_settings.json: {ex.Message}");
            _topicSettings = new Dictionary<long, int>();
        }
    }

    private static void SaveTopicSettings()
    {
        try
        {
            lock (_fileLock)
            {
                var json = JsonConvert.SerializeObject(_topicSettings, Formatting.Indented);
                File.WriteAllText(_topicSettingsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения topic_settings.json: {ex.Message}");
        }
    }

    private static int? GetTopicIdForGroup(long groupId)
    {
        if (_topicSettings.ContainsKey(groupId))
        {
            return _topicSettings[groupId];
        }
        return null;
    }

    private static async Task<bool> SetTopicForGroup(long groupId, int topicId, long adminId)
    {
        if (!IsSuperAdmin(adminId) && !IsOwner(adminId))
            return false;

        _topicSettings[groupId] = topicId;
        SaveTopicSettings();

        LogInfo($"Админ {adminId} установил топик {topicId} для группы {groupId}");
        return true;
    }

    private static async Task<bool> ClearTopicForGroup(long groupId, long adminId)
    {
        if (!IsSuperAdmin(adminId) && !IsOwner(adminId))
            return false;

        if (_topicSettings.ContainsKey(groupId))
        {
            _topicSettings.Remove(groupId);
            SaveTopicSettings();

            LogInfo($"Админ {adminId} очистил настройки топика для группы {groupId}");
            return true;
        }
        return false;
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Логирование
    // ============================

    private static void LogError(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(logMessage);

        try
        {
            lock (_fileLock)
            {
                File.AppendAllText("error.log", logMessage + Environment.NewLine);
            }
        }
        catch
        {
            // Игнорируем ошибки записи в лог
        }
    }

    private static void LogInfo(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(logMessage);
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Работа с файлами
    // ============================

    private static void CacheImageFiles()
    {
        foreach (var folder in _imageFolders)
        {
            if (!Directory.Exists(folder.Value))
            {
                _imageFilesCache[folder.Key] = Array.Empty<string>();
                continue;
            }

            try
            {
                var files = Directory.GetFiles(folder.Value, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                _imageFilesCache[folder.Key] = files;
                LogInfo($"Кэшировано {files.Length} файлов для редкости {folder.Key}");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при кэшировании файлов для {folder.Key}: {ex.Message}");
                _imageFilesCache[folder.Key] = Array.Empty<string>();
            }
        }
    }

    private static void CheckImageFolders()
    {
        var totalImages = 0;

        foreach (var folder in _imageFolders)
        {
            Console.WriteLine($"\n[{folder.Key.ToUpper()}]");
            Console.WriteLine($"Путь: {folder.Value}");

            if (!Directory.Exists(folder.Value))
            {
                Console.WriteLine("❌ Папка не существует!");
                Directory.CreateDirectory(folder.Value);
                Console.WriteLine("✅ Папка создана автоматически");
                continue;
            }

            try
            {
                var files = Directory.GetFiles(folder.Value, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                totalImages += files.Length;
                Console.WriteLine($"✅ Изображений: {files.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка чтения папки: {ex.Message}");
            }
        }

        Console.WriteLine($"\n📊 Всего изображений: {totalImages}");

        if (totalImages == 0)
        {
            Console.WriteLine("\n⚠️ ВНИМАНИЕ: Не найдено ни одного изображения!");
            Console.WriteLine("Бот не сможет отправлять карты.");
        }
    }

    private static BotConfig LoadConfig()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_configFile))
                {
                    var json = File.ReadAllText(_configFile);
                    var config = JsonConvert.DeserializeObject<BotConfig>(json) ?? new BotConfig();

                    if (!string.IsNullOrEmpty(DotNetEnv.Env.GetString("BOT_TOKEN")))
                        config.BotToken = DotNetEnv.Env.GetString("BOT_TOKEN");

                    if (!string.IsNullOrEmpty(config.BotToken) && !config.BotToken.Contains(':'))
                    {
                        LogError("Токен имеет некорректный формат");
                        config.BotToken = "";
                    }

                    return config;
                }
                else
                {
                    var config = new BotConfig { BotToken = "" };
                    var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                    File.WriteAllText(_configFile, json);
                    LogInfo("Создан новый конфигурационный файл");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки конфигурации: {ex.Message}");
            return new BotConfig();
        }
    }

    private static void LoadUserCards()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_userCardsFile))
                {
                    var json = File.ReadAllText(_userCardsFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, List<string>>>(json)
                                 ?? new Dictionary<long, List<string>>();

                    _userCards = new ConcurrentDictionary<long, HashSet<string>>();
                    foreach (var kvp in tempDict)
                    {
                        _userCards[kvp.Key] = new HashSet<string>(kvp.Value);
                    }
                }
                else
                {
                    _userCards = new ConcurrentDictionary<long, HashSet<string>>();
                }
            }
            LogInfo($"Загружено {_userCards.Count} записей о картах пользователей");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки usercards.json: {ex.Message}");
            _userCards = new ConcurrentDictionary<long, HashSet<string>>();
        }
    }

    private static void LoadUserDecks()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_userDecksFile))
                {
                    var json = File.ReadAllText(_userDecksFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, UserDeck>>(json)
                                 ?? new Dictionary<long, UserDeck>();

                    _userDecks = new ConcurrentDictionary<long, UserDeck>(tempDict);
                }
                else
                {
                    _userDecks = new ConcurrentDictionary<long, UserDeck>();
                }
            }
            LogInfo($"Загружено {_userDecks.Count} колод пользователей");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки userdecks.json: {ex.Message}");
            _userDecks = new ConcurrentDictionary<long, UserDeck>();
        }
    }

    private static void SaveUserCards()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _userCards.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToList()
                );

                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_userCardsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения usercards.json: {ex.Message}");
        }
    }

    private static void SaveUserDecks()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _userDecks.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                );

                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_userDecksFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения userdecks.json: {ex.Message}");
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Администрация
    // ============================

    private static void LoadAdmins()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_adminsFile))
                {
                    var json = File.ReadAllText(_adminsFile);
                    var adminList = JsonConvert.DeserializeObject<List<AdminData>>(json) ?? new List<AdminData>();

                    _admins = new ConcurrentDictionary<long, AdminData>();
                    foreach (var admin in adminList)
                    {
                        _admins[admin.UserId] = admin;
                    }
                }
                else
                {
                    _admins = new ConcurrentDictionary<long, AdminData>();
                }
            }
            LogInfo($"Загружено {_admins.Count} администраторов");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки admins.json: {ex.Message}");
            _admins = new ConcurrentDictionary<long, AdminData>();
        }
    }

    private static void SaveAdmins()
    {
        try
        {
            lock (_fileLock)
            {
                var adminList = _admins.Values.ToList();
                var json = JsonConvert.SerializeObject(adminList, Formatting.Indented);
                File.WriteAllText(_adminsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения admins.json: {ex.Message}");
        }
    }

    private static bool IsOwner(long userId)
    {
        return userId == _ownerId;
    }

    private static bool IsAdmin(long userId)
    {
        return IsOwner(userId) || _admins.ContainsKey(userId);
    }

    private static bool IsSuperAdmin(long userId)
    {
        if (IsOwner(userId)) return true;
        if (_admins.TryGetValue(userId, out var admin))
        {
            return admin.Level >= AdminLevel.SuperAdmin;
        }
        return false;
    }

    private static AdminLevel? GetAdminLevel(long userId)
    {
        if (IsOwner(userId)) return AdminLevel.Owner;
        if (_admins.TryGetValue(userId, out var admin))
        {
            return admin.Level;
        }
        return null;
    }

    private static async Task<bool> AddAdmin(long userId, string username, string firstName, AdminLevel level, long addedBy)
    {
        if (!IsAdmin(addedBy))
            return false;

        if (_admins.ContainsKey(userId))
            return false;

        var admin = new AdminData
        {
            UserId = userId,
            Username = username ?? "",
            FirstName = firstName ?? "",
            AddedBy = addedBy,
            AddedDate = DateTime.UtcNow,
            Level = level
        };

        _admins[userId] = admin;
        SaveAdmins();

        // Логируем действие
        var adderName = IsOwner(addedBy) ? "Владелец" : $"Админ {addedBy}";
        LogInfo($"{adderName} назначил администратором {userId} (@{username}) с уровнем {level}");

        return true;
    }

    private static async Task<bool> RemoveAdmin(long userId, long removedBy)
    {
        if (!IsAdmin(removedBy))
            return false;

        if (IsOwner(userId))
            return false; // Нельзя удалить владельца

        if (IsAdmin(removedBy) && !IsSuperAdmin(removedBy) && IsSuperAdmin(userId))
            return false; // Обычный админ не может удалить суперадмина

        if (!_admins.ContainsKey(userId))
            return false;

        var success = _admins.TryRemove(userId, out _);
        if (success)
        {
            SaveAdmins();

            // Логируем действие
            var removerName = IsOwner(removedBy) ? "Владелец" : $"Админ {removedBy}";
            LogInfo($"{removerName} удалил администратора {userId}");
        }

        return success;
    }

    private static bool ChangeAdminLevel(long userId, AdminLevel newLevel, long changedBy)
    {
        if (!IsAdmin(changedBy))
            return false;

        if (!_admins.ContainsKey(userId))
            return false;

        if (IsOwner(userId))
            return false; // Нельзя изменить уровень владельца

        if (IsAdmin(changedBy) && !IsSuperAdmin(changedBy))
            return false; // Только суперадмин может менять уровни

        var admin = _admins[userId];
        admin.Level = newLevel;
        _admins[userId] = admin;
        SaveAdmins();

        // Логируем действие
        var changerName = IsOwner(changedBy) ? "Владелец" : $"Суперадмин {changedBy}";
        LogInfo($"{changerName} изменил уровень администратора {userId} на {newLevel}");

        return true;
    }

    private static string GetAdminList()
    {
        var ownerInfo = $"👑 <b>Владелец:</b>\n" +
                       $"   👤 ID: <code>{_ownerId}</code>\n\n";

        var adminsList = _admins.Values
            .OrderByDescending(a => a.Level)
            .ThenBy(a => a.AddedDate)
            .ToList();

        if (adminsList.Count == 0)
        {
            return ownerInfo + "📋 <b>Администраторы:</b>\n   Нет назначенных администраторов";
        }

        var result = ownerInfo + "📋 <b>Администраторы:</b>\n";

        for (int i = 0; i < adminsList.Count; i++)
        {
            var admin = adminsList[i];
            var levelEmoji = admin.Level switch
            {
                AdminLevel.Admin => "👮",
                AdminLevel.SuperAdmin => "🛡️",
                _ => "👤"
            };

            var levelName = admin.Level switch
            {
                AdminLevel.Admin => "Админ",
                AdminLevel.SuperAdmin => "Суперадмин",
                _ => "Неизвестно"
            };

            result += $"\n{levelEmoji} <b>{i + 1}.</b> {admin.FirstName}";
            if (!string.IsNullOrEmpty(admin.Username))
                result += $" (@{admin.Username})";

            result += $"\n   📊 Уровень: <b>{levelName}</b>";
            result += $"\n   🆔 ID: <code>{admin.UserId}</code>";
            result += $"\n   📅 Назначен: {admin.AddedDate:dd.MM.yyyy}";
            result += $"\n   👥 Назначил: <code>{admin.AddedBy}</code>";
        }

        return result;
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Создание бэкапа
    // ============================

    private static void CreateBackup()
    {
        try
        {
            var backupDir = Path.Combine(Path.GetDirectoryName(_usersFile), "backup");
            if (!Directory.Exists(backupDir))
                Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            var backupFiles = new[] { _usersFile, _userCardsFile, _userDecksFile, _adminsFile, _topicSettingsFile };
            foreach (var file in backupFiles)
            {
                if (File.Exists(file))
                {
                    var backupFile = Path.Combine(backupDir, $"{Path.GetFileName(file)}.{timestamp}.bak");
                    File.Copy(file, backupFile, true);
                }
            }

            LogInfo($"Создана резервная копия данных");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка создания резервной копии: {ex.Message}");
        }
    }

    private static void SaveAllData()
    {
        try
        {
            SaveUsers();
            SaveUserCards();
            SaveUserDecks();
            SaveUserGems();
            SaveAdmins();
            SaveUserChestStats();
            SavePromoCodes();
            SaveTopicSettings();
            SaveUserEvolutions();
            LogInfo("Все данные сохранены");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка при сохранении данных: {ex.Message}");
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Обработка обновлений
    // ============================

    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            int? messageThreadId = null;
            long chatId = 0;
            Chat chat = null;

            // Безопасное получение данных из обновления
            if (update.Message != null)
            {
                chatId = update.Message.Chat.Id;
                messageThreadId = update.Message.MessageThreadId;
                chat = update.Message.Chat;
            }
            else if (update.CallbackQuery != null)
            {
                // Для callback-запросов получаем данные безопасно
                chat = update.CallbackQuery.Message?.Chat;
                if (chat != null)
                {
                    chatId = chat.Id;
                    messageThreadId = update.CallbackQuery.Message?.MessageThreadId;
                }
            }

            if (chatId == 0) // Если не удалось получить chatId, выходим
                return;

            // Получаем пользователя безопасно
            User fromUser = null;
            long userId = 0;

            if (update.Message != null)
            {
                fromUser = update.Message.From;
            }
            else if (update.CallbackQuery != null)
            {
                fromUser = update.CallbackQuery.From;
            }

            if (fromUser == null)
                return;

            userId = fromUser.Id;

            // Обработка callback запросов (они всегда идут после нажатия кнопки)
            if (update.CallbackQuery != null)
            {
                // Проверяем топик для callback'ов в группах
                if (chatId < 0) // Это группа
                {
                    var topicId = GetTopicIdForGroup(chatId);
                    if (topicId.HasValue && messageThreadId != topicId.Value)
                    {
                        // Отправляем уведомление о неправильном топике
                        await bot.AnswerCallbackQuery(
                            callbackQueryId: update.CallbackQuery.Id,
                            text: $"⚠️ Бот работает только в топике #{topicId.Value}",
                            showAlert: true,
                            cancellationToken: ct
                        );
                        return;
                    }
                }

                // Безопасная обработка callback
                await HandleCallbackQuery(bot, messageThreadId, update.CallbackQuery, ct);
                return;
            }

            if (update.Message is not { } message)
                return;

            var text = message.Text ?? "";

            // Сначала проверяем, не содержит ли сообщение "пирожки" (это может быть не командой)
            var (containsPirozhki, isNegative) = AnalyzePirozhkiMessage(text);
            if (containsPirozhki)
            {
                await ReactToPirozhki(bot, message, messageThreadId ?? 0, ct);
            }

            string command = ParseCommand(text);

            // Если нет команды, просто выходим (не отвечаем)
            if (string.IsNullOrEmpty(command))
                return;

            // ТЕПЕРЬ проверяем топик ТОЛЬКО если есть команда
            if (chatId < 0) // Это группа
            {
                var topicId = GetTopicIdForGroup(chatId);

                // Проверяем, установлен ли топик для этой группы
                if (topicId.HasValue)
                {
                    // Если команда НЕ из нужного топика
                    if (messageThreadId != topicId.Value)
                    {
                        // Отправляем одноразовое уведомление о неправильном топике
                        try
                        {
                            await bot.SendMessage(
                                chatId: chatId,
                                messageThreadId: messageThreadId,
                                text: $"⚠️ <b>Бот работает только в топике #{topicId.Value}!</b>\n\n" +
                                      $"Пожалуйста, используйте команды бота в указанном топике.\n" +
                                      $"В этом топике команды не обрабатываются.",
                                parseMode: ParseMode.Html,
                                cancellationToken: ct
                            );
                        }
                        catch (Exception ex)
                        {
                            LogError($"Ошибка при отправке уведомления о топике: {ex.Message}");
                        }

                        // Игнорируем команду
                        return;
                    }
                }
                // Если топик НЕ установлен для группы, пропускаем команду (бот работает во всех топиках)
            }

            EnsureUserExists(userId, fromUser);

            // Проверяем, является ли команда админской
            if (IsAdminCommand(command))
            {
                // Админские команды обрабатываем везде
                await RouteAdminCommand(bot, command, chatId, userId, message, ct);
                return;
            }

            // Команды, которые работают без подписки
            var noSubscriptionCommands = new[] { "start", "help" };

            if (noSubscriptionCommands.Contains(command))
            {
                // Эти команды работают без проверки подписки
                await RouteCommand(bot, command, chatId, userId, message, ct);
                return;
            }

            // Проверяем, существует ли такая обычная команда
            if (!IsValidUserCommand(command))
            {
                // Неизвестная команда - ничего не делаем
                return;
            }

            // ИЗМЕНЕНО: Проверяем только подписку на канал
            var isChannelMember = await CheckMemberships(userId, ct);

            if (!isChannelMember)
            {
                string missingLinks = MessageManager.Get("access_denied_channel", _channelLink);

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: MessageManager.Get("access_denied", missingLinks),
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "check_membership")
                    }
                    }),
                    cancellationToken: ct
                );
                return;
            }

            // Все обычные команды обрабатываются
            await RouteCommand(bot, command, chatId, userId, message, ct);
        }
        catch (Exception ex)
        {
            LogError($"Ошибка обработки обновления: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }

    private static bool IsValidUserCommand(string command)
    {
        var validCommands = new[]
        {
        "start",
        "help",
        "pic",
        "card",
        "top",
        "top_money",
        "top_card",
        "gold",
        "balance",
        "stats",
        "mycards",
        "chance",
        "prob",
        "cooldown",
        "cd",
        "profile",
        "профиль",
        "deck",
        "колода",
        "promo",
        "shop",
        "buy",
        "convert",
        "gems",
        "chestlimit",
        "limit",
        "лимит",
        "cards",          // ДОЛЖНО БЫТЬ
        "view",           // ДОЛЖНО БЫТЬ
        "showcard",       // ДОЛЖНО БЫТЬ
        "evolutions",
        "myevolutions",
        "evolution",
        "эволюции",
        "моиэволюции",
        "donate"
    };

        return validCommands.Contains(command);
    }
    // Проверяет, содержит ли сообщение слово "пирожки" в разных падежах
    private static (bool containsPirozhki, bool isNegative) AnalyzePirozhkiMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (false, false);

        text = text.ToLower().Trim();

        // Список форм слова "пирожки" в разных падежах
        var pirozhkiForms = new[]
        {
        "пирожки", "пирожков", "пирожкам", "пирожками", "пирожках",
        "пирожок", "пирожка", "пирожку", "пирожком", "пирожке",
        "пирожочки", "пирожочков", "пирожочкам", "пирожочками", "пирожочках",
        "пирожочек", "пирожочка", "пирожочку", "пирожочком", "пирожочке"
    };

        // Негативные слова/фразы
        var negativeWords = new[]
        {
        "говно", "гавно", "дерьмо", "херня", "хуйня", "фигня",
        "отстой", "плохо", "плохие", "ужас", "ужасные", "отврат",
        "отвратительные", "мерзость", "не вкусно", "не вкусные",
        "гадость", "тухлые", "испорченные", "протухли", "не свежие",
        "блевот", "рвота", "тошнит", "противно", "фу", "тьфу",
        "засохли", "черствые", "пересолен", "пересоленные",
        "недосол", "недосоленные", "пережарен", "пережаренные",
        "недожарен", "недожаренные", "кислые", "горькие",
        "переперчен", "переперченные", "пидор", "пидорас", "какашка",
        "говняшка", "лох", "лохов", "даун", "даунов", "педик", "педиков",
        "утютю", "малыш", "малышей", "глупый", "глупых", "газонюх", "газонюхов",
        "куплинов", "соник", "соникофан", "соникофанов",
        // Дополнительные просторечные и грубые выражения
        "дрянь", "дрянные", "отврат", "отвратительно", "омерзительно",
        "сраные", "срань", "залупа", "залупленные", "сиськи", "письки",
        "блядские", "бля", "блять", "сучие", "сучьи", "падла", "падлы",
        "мусор", "мусорные", "выбросить", "выкинуть", "в помойку",
    
        // О качестве
        "резиновые", "как резина", "дубовые", "как дерево", "картонные",
        "сырые", "сыроватые", "перепеченные", "подгорелые", "сожженные",
        "непропеченные", "холодные", "ледяные", "остывшие", "жирные",
        "жирноватые", "сухие", "пересушенные", "безвкусные", "пресные",
        "кислятина", "горьковатые", "соленые", "пересоленные", "соль перебивает",
        "с душком", "с запахом", "несъедобные", "невкусные", "никакие",
    
        // Эмоциональные реакции
        "беее", "фе", "тьфу ты", "ужас-ужас", "кошмар", "кошмарные",
        "не понравились", "разочарование", "разочаровал", "разочаровала",
        "ожидал большего", "деньги на ветер", "зря купил", "зря потратился",
        "не советую", "не рекомендую", "обман", "обманули", "кидалово",
        "развод", "лохотрон", "наелись", "объелись", "тяжелые",
    
        // Сравнения
        "хуже некуда", "хуже не бывает", "хуже прежнего", "хуже прошлого раза",
        "как в столовке", "как в школьной столовой", "как в больничной столовой",
        "как из микроволновки", "как магазинные замороженные", "как консервы",
    
        // Состояние
        "червивые", "с волосами", "с мухами", "с тараканами", "грязные",
        "немытые", "с песком", "хрустит на зубах", "с костями", "с скорлупой",
        "испортились", "с истекшим сроком", "просроченные", "левые",
        "ненастоящие", "поддельные", "дешевые", "дешевка", "эконом вариант",
    
        // Дополнительные грубые
        "зашквар", "зашкварные", "позор", "позорные", "стыд", "стыдно подавать",
        "содрали кожу", "кожу содрали", "ободрали", "обманка", "липа",
    
        // Современный сленг
        "кринж", "кринжовые", "facepalm", "fail", "эпик фейл",
        "полный провал", "промашка", "мимо кассы",
    
        // Кулинарные провалы
        "недопеченные", "непропеченные", "сырое тесто", "мука чувствуется",
        "мало начинки", "пустые", "только тесто", "переперченные",
        "специи забивают", "чеснока много", "лука много", "масляные",
        "масло льется", "жир стекает", "прилипают к небу", "пристают к зубам",
    
        // Гигиена/внешний вид
        "кривые", "корявые", "разваливаются", "размокли", "помятые",
        "бледные", "неаппетитные", "непривлекательные", "страшные",
    };

        // Позитивные слова/фразы
        var positiveWords = new[]
        {
        "вкусно", "вкусные", "класс", "классные", "супер",
        "отлично", "отличные", "прекрасно", "прекрасные",
        "замечательно", "замечательные", "обалденно", "обалденные",
        "шикарно", "шикарные", "великолепно", "великолепные",
        "люблю", "нравятся", "обожаю", "восхитительно", "восхитительные",
        "нежные", "сочные", "ароматные", "аппетитно", "аппетитные",
        "свежие", "теплые", "горячие", "только из печи", "с пылу с жару",
        "тают во рту", "мягкие", "пышные", "румяные", "золотистые"
    };

        // Проверяем отдельные слова
        var words = text.Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '—', '–', '"', '\'', '`' },
            StringSplitOptions.RemoveEmptyEntries);

        bool containsPirozhki = false;
        int negativeCount = 0;
        int positiveCount = 0;

        foreach (var word in words)
        {
            var cleanedWord = word.ToLower();

            // Проверяем на пирожки
            if (pirozhkiForms.Contains(cleanedWord))
            {
                containsPirozhki = true;
            }

            // Проверяем негативные слова
            if (negativeWords.Contains(cleanedWord))
            {
                negativeCount++;
            }

            // Проверяем позитивные слова
            if (positiveWords.Contains(cleanedWord))
            {
                positiveCount++;
            }
        }

        // Также проверяем комбинации слов (фразы)
        foreach (var negativeWord in negativeWords)
        {
            if (text.Contains(negativeWord))
            {
                negativeCount++;
            }
        }

        foreach (var positiveWord in positiveWords)
        {
            if (text.Contains(positiveWord))
            {
                positiveCount++;
            }
        }

        // Определяем общее настроение
        bool isNegative = negativeCount > positiveCount;

        return (containsPirozhki, isNegative);
    }

    // Ставит реакцию на сообщение с "пирожки"
    private static async Task ReactToPirozhki(ITelegramBotClient bot, Message message, int messageThreadId, CancellationToken ct)
    {
        try
        {
            var (containsPirozhki, isNegative) = AnalyzePirozhkiMessage(message.Text);

            if (!containsPirozhki)
                return;

            // Проверяем, что сообщение из группы
            if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
                return;

            // Выбираем реакцию
            ReactionType reaction;
            string responseMessage = "";

            if (isNegative)
            {
                // Негативные реакции и фразы
                var negativeOptions = new[]
                {
                (emoji: "👎", phrase: "А я вот считаю, что пирожки - это искусство! 🎨"),
                (emoji: "😡", phrase: "Пирожки обиделись и ушли... 🥺"),
                (emoji: "🤬", phrase: "Зато душой не торгуем! 🫡"),
                (emoji: "👎", phrase: "А сам-то пробовал готовить? 😤"),
                (emoji: "😡", phrase: "Не расстраивай пирожки, они старались! 🥟"),
                (emoji: "🤬", phrase: "Это потому что мама не научила ценить пирожки? 👩‍🍳"),
                (emoji: "👎", phrase: "Зашкварное мнение, даже пирожки возмущены! 🤯"),
                (emoji: "😡", phrase: "С таким мнением тебе в корзину! 🚮"),
                (emoji: "🤬", phrase: "Пирожки требуют реванша! ⚔️"),
                (emoji: "👎", phrase: "Я запомнил твои слова... пирожки не простят! 👹")
            };

                var option = negativeOptions[_random.Next(negativeOptions.Length)];
                reaction = new ReactionTypeEmoji { Emoji = option.emoji };
                responseMessage = option.phrase;
            }
            else
            {
                // Надежные позитивные реакции
                var positiveReactions = new[]
                {
                new ReactionTypeEmoji { Emoji = "👍" },
                new ReactionTypeEmoji { Emoji = "❤️" },
                new ReactionTypeEmoji { Emoji = "🔥" },
                new ReactionTypeEmoji { Emoji = "👏" }
            };
                reaction = positiveReactions[_random.Next(positiveReactions.Length)];
            }

            // Устанавливаем реакцию
            await bot.SetMessageReaction(
                chatId: message.Chat.Id,
                messageId: message.MessageId,
                reaction: new[] { reaction },
                cancellationToken: ct
            );

            LogInfo($"Реакция {((ReactionTypeEmoji)reaction).Emoji} поставлена на сообщение {message.MessageId}");

            // С вероятностью 30% отправляем ответную фразу
            if (_random.Next(100) < 75 && !string.IsNullOrEmpty(responseMessage))
            {
                await Task.Delay(1000, ct);

                // Добавляем случайную задержку для естественности
                await Task.Delay(_random.Next(200, 500), ct);

                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    messageThreadId: messageThreadId,
                    text: responseMessage,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct
                );

                LogInfo($"Отправлена фраза на сообщение {message.MessageId}: {responseMessage}");
            }
        }
        catch (ApiRequestException ex)
        {
            LogError($"API ошибка реакции: {ex.ErrorCode} - {ex.Message}");
        }
        catch (Exception ex)
        {
            LogError($"Общая ошибка при установке реакции: {ex.Message}");
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Проверка членства
    // ============================

    private static async Task<bool> CheckMemberships(long userId, CancellationToken ct)
    {
        try
        {
            // Проверяем кэш
            if (_subscriptionCache.TryGetValue(userId, out var cached))
            {
                // Если кэш ещё валиден (не прошло CACHE_TTL), возвращаем сохранённое значение
                if (DateTime.UtcNow - cached.checkedAt < CACHE_TTL)
                {
                    return cached.isSubscribed;
                }
            }

            // Если кэша нет или он устарел, проверяем через API
            var isChannelMember = await IsUserMemberOfChat(userId, _channelId, ct);

            // Обновляем кэш
            _subscriptionCache[userId] = (isChannelMember, DateTime.UtcNow);

            LogInfo($"Проверка подписки для пользователя {userId}: {(isChannelMember ? "✅ подписан" : "❌ не подписан")}");

            return isChannelMember;
        }
        catch (Exception ex)
        {
            LogError($"Ошибка проверки подписки для пользователя {userId}: {ex.Message}");

            // В случае ошибки, если есть кэш - используем его, иначе считаем что не подписан
            if (_subscriptionCache.TryGetValue(userId, out var cached))
            {
                return cached.isSubscribed;
            }
            return false;
        }
    }

    private static async Task<bool> IsUserMemberOfChat(long userId, long chatId, CancellationToken ct)
    {
        try
        {
            var chatMember = await _bot.GetChatMember(
                chatId: chatId,
                userId: userId,
                cancellationToken: ct
            );

            return chatMember.Status is
                ChatMemberStatus.Member or
                ChatMemberStatus.Administrator or
                ChatMemberStatus.Creator;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // Пользователь не найден в чате
            LogInfo($"Пользователь {userId} не найден в чате {chatId}");
            return false;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            // Бот заблокировал пользователя или пользователь заблокировал бота
            LogInfo($"Доступ запрещён для пользователя {userId} в чате {chatId}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Ошибка проверки членства пользователя {userId} в чате {chatId}: {ex.Message}");
            return false;
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Парсинг команд
    // ============================

    private static string ParseCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        text = text.Trim();

        // Специальная проверка для /a (админское меню)
        if (text.Equals("/a", StringComparison.OrdinalIgnoreCase) ||
            text.Equals($"/a@{_botUsername}", StringComparison.OrdinalIgnoreCase))
        {
            return "admin_menu";
        }

        string command = null;
        string[] parts = Array.Empty<string>();

        // Проверяем формат /command@botname
        if (text.StartsWith("/") && text.Contains("@"))
        {
            parts = text.Split(new[] { '@' }, 2);
            if (parts.Length == 2 && parts[1].Equals(_botUsername, StringComparison.OrdinalIgnoreCase))
            {
                // Извлекаем команду без @botname
                var commandPart = parts[0].Substring(1).Trim();
                var spaceIndex = commandPart.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    command = commandPart.Substring(0, spaceIndex).ToLower();
                }
                else
                {
                    command = commandPart.ToLower();
                }
            }
        }
        // Проверяем формат /command
        else if (text.StartsWith("/"))
        {
            var commandPart = text.Substring(1).Trim();
            var spaceIndex = commandPart.IndexOf(' ');
            if (spaceIndex > 0)
            {
                command = commandPart.Substring(0, spaceIndex).ToLower();
            }
            else
            {
                command = commandPart.ToLower();
            }
        }
        // Проверяем формат @botname /command
        else if (text.StartsWith($"@{_botUsername}", StringComparison.OrdinalIgnoreCase))
        {
            var afterBotname = text.Substring(_botUsername.Length + 1).Trim();
            if (afterBotname.StartsWith("/"))
            {
                command = ParseCommand(afterBotname);
            }
        }

        return command;
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Парсинг команд (обновить метод IsAdminCommand)
    // ============================

    private static bool IsAdminCommand(string command)
    {
        var adminCommands = new[]
        {
            "admin", "ahelp", "a", "admin_menu",
            "addadmin", "removeadmin", "admins",
            "addgold", "removegold", "setgold", "addcard", "removecard",
            "resetcd", "resetstats", "deleteuser", "setadminlevel",
            "broadcast", "statsall", "finduser", "userinfo",
            "sendtochannel", "sendphoto", "pinmessage", "unpinmessage",
            "addpromo", "removepromo", "listpromos",
            "topicset", "topicclear", "topicinfo", // Добавлены команды управления топиками
            "addevolution", "removeevolution", "resetevolutions", "listevolutions",
            "clearcache", "clearcacheuser" 
                            
        };

        return adminCommands.Contains(command);
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Callback обработчики
    // ============================

    private static async Task HandleCallbackQuery(ITelegramBotClient bot, int? messageThreadId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message?.Chat.Id ?? 0;
        var userId = callbackQuery.From.Id;
        var data = callbackQuery.Data;
        var messageId = callbackQuery.Message?.MessageId ?? 0;

        // Получаем messageThreadId из сообщения, если не передан
        var threadId = messageThreadId ?? callbackQuery.Message?.MessageThreadId;

        if (callbackQuery.Message == null || chatId == 0)
            return;

        // Проверка топика для callback'ов в группах
        if (chatId < 0) // Это группа
        {
            var topicId = GetTopicIdForGroup(chatId);
            if (topicId.HasValue && threadId != topicId.Value)
            {
                // Отправляем ответ в нужный топик
                await bot.AnswerCallbackQuery(
                    callbackQueryId: callbackQuery.Id,
                    text: $"⚠️ Бот работает только в топике #{topicId.Value}",
                    cancellationToken: ct
                );
                return;
            }
        }

        EnsureUserExists(userId, callbackQuery.From);

        try
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

            if (string.IsNullOrEmpty(data))
                return;

            // ВСЕ callback-обработчики теперь получают threadId
            // 1. Обработка кнопок помощи
            if (data.StartsWith("help_"))
            {
                await HandleHelpCallbackQuery(bot, callbackQuery, threadId, ct);
                return;
            }

            // 2. Обработка кнопок профилей бота (не Telegram)
            if (data.StartsWith("bot_profile_"))
            {
                await HandleBotProfileCallbackQuery(bot, threadId, callbackQuery, ct);
                return;
            }

            // 3. Обработка покупки сундуков
            if (data.StartsWith("buy_"))
            {
                var chestType = data.Substring(4);
                if (_chestRarityChances.ContainsKey(chestType))
                {
                    await OpenChest(bot, chatId, userId, chestType, threadId, ct);
                }
                return;
            }

            if (data.StartsWith("cards_"))
            {
                await HandleCardsCallbackQuery(bot, chatId, userId, data, messageId, messageThreadId, ct);
                return;
            }

            // Обработка callback для просмотра конкретной карты
            if (data.StartsWith("view_"))
            {
                await HandleViewCardCallbackQuery(bot, chatId, userId, data, messageId, messageThreadId, ct);
                return;
            }

            if (data.StartsWith("view_evo_"))
            {
                await HandleViewEvolutionCallback(bot, chatId, userId, data, messageThreadId, ct);
                return;
            }

            // 4. Обработка других callback'ов
            switch (data)
            {
                case "help":
                case "help_menu":
                    await SendHelpMenu(bot, chatId, userId, threadId, messageId, ct);
                    break;

                case "check_membership":
                    await CheckMembershipAndRespond(bot, threadId, callbackQuery, ct);
                    break;

                case "top_money":
                case "top_money_menu":
                    await SendLeaderboardByMoney(bot, chatId, userId, threadId, ct, messageId);
                    break;

                case "top_card":
                case "top_card_menu":
                    await SendLeaderboardByCards(bot, chatId, userId, threadId, ct, messageId);
                    break;

                case "refresh_top":
                case "refresh_top_menu":
                    await ShowTopMenu(bot, chatId, userId, threadId, ct, messageId);
                    break;

                case "refresh_money":
                case "refresh_top_money":
                    await SendLeaderboardByMoney(bot, chatId, userId, threadId, ct, messageId);
                    break;

                case "refresh_cards":
                case "refresh_top_cards":
                    await SendLeaderboardByCards(bot, chatId, userId, threadId, ct, messageId);
                    break;

                case "refresh_shop":
                    try
                    {
                        await bot.DeleteMessage(
                            chatId: chatId,
                            messageId: messageId,
                            cancellationToken: ct
                        );
                    }
                    catch { }
                    await SendShopMenu(bot, chatId, userId, threadId, ct);
                    break;

                case "convert_gold":
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: threadId, // Используем threadId
                        text: "💎 <b>Обмен золота на гемы</b>\n\n" +
                             "Для обмена золота на гемы используйте команду:\n" +
                             "<code>/convert 100</code> - обменять 100 золота на 10 гемов\n\n" +
                             "Минимальная сумма: 10 золота\n" +
                             "Сумма должна быть кратна 10\n\n" +
                             "Примеры:\n" +
                             "<code>/convert 50</code> - 5 гемов\n" +
                             "<code>/convert 100</code> - 10 гемов\n" +
                             "<code>/convert 500</code> - 50 гемов",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    break;

                case "show_balance":
                    await SendUserGemsBalance(bot, chatId, userId, threadId, ct);
                    break;

                case "chest_limit":
                    await SendChestLimitInfo(bot, chatId, userId, threadId, ct);
                    break;


                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка обработки callback: {ex.Message}");
        }
    }

    // ============================
    // ОБРАБОТЧИК КНОПОК ПРОФИЛЕЙ В БОТЕ (из топа)
    // ============================

    private static async Task HandleBotProfileCallbackQuery(ITelegramBotClient bot, int? messageThreadId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message.Chat.Id;
        var userId = callbackQuery.From.Id;
        var data = callbackQuery.Data;
        var messageId = callbackQuery.Message.MessageId;

        // Получаем threadId из сообщения
        var threadId = messageThreadId ?? callbackQuery.Message.MessageThreadId;

        // Проверка топика для callback'ов в группах
        if (chatId < 0) // Это группа
        {
            var topicId = GetTopicIdForGroup(chatId);
            if (topicId.HasValue && threadId != topicId.Value)
            {
                // Отправляем ответ в нужный топик
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: topicId.Value,
                    text: $"⚠️ Бот работает только в топике #{topicId.Value}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }
        }

        // Извлекаем ID пользователя из callback data
        var parts = data.Split('_');
        if (parts.Length < 3 || !long.TryParse(parts[2], out var targetUserId))
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                text: "❌ Ошибка получения профиля",
                cancellationToken: ct
            );
            return;
        }

        // Проверяем подписку текущего пользователя
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            await bot.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                text: "❌ Подпишитесь на канал",
                cancellationToken: ct
            );

            // Отправляем сообщение о необходимости подписки
            string missingLinks = MessageManager.Get("access_denied_channel", _channelLink);

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: threadId,
                text: MessageManager.Get("access_denied", missingLinks),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                new[]
                {
                    InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "check_membership")
                }
                }),
                cancellationToken: ct
            );
            return;
        }

        var targetUser = _users[targetUserId];
        var cardCount = _userCards.ContainsKey(targetUserId) ? _userCards[targetUserId].Count : 0;
        var isAdmin = IsAdmin(targetUserId);
        var adminLevel = GetAdminLevel(targetUserId);
        var totalPossibleCards = 0;

        foreach (var rarity in _imageFilesCache.Keys)
        {
            totalPossibleCards += _imageFilesCache[rarity].Length;
        }

        // Получаем информацию о колоде
        var deck = _userDecks.ContainsKey(targetUserId) ? _userDecks[targetUserId] : new UserDeck();
        var deckSize = deck.CardIds.Count;

        // Рассчитываем процент заполнения коллекции
        var collectionPercentage = totalPossibleCards > 0
            ? Math.Round((double)cardCount / totalPossibleCards * 100, 1)
            : 0;

        // Получаем информацию о любимой карте
        string favoriteCardInfo = "";
        if (!string.IsNullOrEmpty(deck.FavoriteCardId))
        {
            var favoriteCardName = GetCardName(deck.FavoriteCardId);
            var favoriteCardRarity = GetCardRarityInfo(deck.FavoriteCardId);
            favoriteCardInfo = $"{favoriteCardRarity.emoji} <b>{favoriteCardName}</b>";
        }
        else
        {
            favoriteCardInfo = "Не выбрана";
        }

        // Определяем место в топе
        var topUsers = _users.Values
            .Where(u => u.Gold > 0)
            .OrderByDescending(u => u.Gold)
            .ThenByDescending(u => u.TotalCards)
            .ToList();

        var rank = topUsers.FindIndex(u => u.UserId == targetUserId) + 1;
        var rankText = rank > 0 ? $"🏆 Место в топе: #{rank}" : "🏆 Не в топе";

        // Получаем информацию о Telegram-аккаунте (без @)
        string telegramInfo = "";
        if (!string.IsNullOrEmpty(targetUser.Username) && targetUser.Username != "null")
        {
            telegramInfo = $"<b>Telegram:</b> <a href=\"https://t.me/{targetUser.Username}\">{targetUser.Username}</a>\n";
        }

        // Формируем имя пользователя без @
        string displayName = "";
        if (!string.IsNullOrEmpty(targetUser.FirstName))
        {
            displayName = targetUser.FirstName;
            if (!string.IsNullOrEmpty(targetUser.LastName))
            {
                displayName += " " + targetUser.LastName;
            }
        }
        else
        {
            displayName = $"Игрок {targetUserId}";
        }

        var profileMessage = $"<b>👤 ПРОФИЛЬ ИГРОКА</b>\n\n"
                           + $"{rankText}\n\n"
                           + $"<b>📛 Игрок:</b> {displayName}\n"
                           + $"{telegramInfo}"
                           + $"<b>🆔 ID в боте:</b> <code>{targetUser.UserId}</code>\n\n"

                           + $"<b>💰 Золото сейчас:</b> <code>{targetUser.Gold}</code>\n"
                           + $"<b>🏦 Золота всего:</b> <code>{targetUser.Gold + CalculateTotalGoldSpent(targetUserId)}</code>\n\n"

                           + $"<b>🎴 Коллекция карт:</b>\n"
                           + $"<b>• Уникальных карт:</b> {cardCount} из {totalPossibleCards} ({collectionPercentage}%)\n"
                           + $"<b>• Всего открыто:</b> {targetUser.TotalCards}\n\n"

                           + $"<b>⚔️ Боевая колода:</b>\n"
                           + $"<b>• Карт в колоде:</b> {deckSize}/{MAX_DECK_SIZE}\n"
                           + $"<b>• ❤️ Любимая карта:</b> {favoriteCardInfo}\n\n"

                           + $"<b>📈 Статистика по редкостям:</b>\n"
                           + $"🔵 Обычные: {targetUser.CommonCards}\n"
                           + $"🟠 Редкие: {targetUser.RareCards}\n"
                           + $"🟣 Эпические: {targetUser.EpicCards}\n"
                           + $"⚪ Легендарные: {targetUser.LegendaryCards}\n"
                           + $"🌟 Чемпионские: {targetUser.ChampionCards}\n\n"

                           + $"<b>👮 Статус:</b> {(isAdmin ? $"Да ({adminLevel})" : "Нет")}\n"
                           + $"<b>📅 Регистрация:</b> {targetUser.Registered:dd.MM.yyyy}\n"
                           + $"<b>⏰ Последняя активность:</b> {(targetUser.LastCardTime == DateTime.MinValue ? "никогда" : targetUser.LastCardTime.ToString("dd.MM.yyyy HH:mm"))}\n";

        // Создаем клавиатуру
        var keyboardButtons = new List<InlineKeyboardButton[]>();

        // Кнопка для перехода в Telegram-профиль (если есть username)
        if (!string.IsNullOrEmpty(targetUser.Username) && targetUser.Username != "null")
        {
            keyboardButtons.Add(new[]
            {
            InlineKeyboardButton.WithUrl("📱 Telegram профиль", $"https://t.me/{targetUser.Username}")
        });
        }
        else
        {
            keyboardButtons.Add(new[]
            {
            InlineKeyboardButton.WithUrl("📱 Написать в Telegram", $"tg://user?id={targetUser.UserId}")
        });
        }

        // Определяем, из какого типа топа пришел запрос
        string backCallback = "";
        if (data.Contains("from_money"))
        {
            backCallback = "top_money";
        }
        else if (data.Contains("from_cards"))
        {
            backCallback = "top_card";
        }

        // Кнопка возврата в топ
        if (!string.IsNullOrEmpty(backCallback))
        {
            keyboardButtons.Add(new[]
            {
            InlineKeyboardButton.WithCallbackData("🔙 Назад к топу", backCallback)
        });
        }

        var inlineKeyboard = new InlineKeyboardMarkup(keyboardButtons);

        // Редактируем текущее сообщение
        try
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: (int)messageId,
                text: profileMessage,
                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard,
                cancellationToken: ct
            );
        }
        catch (ApiRequestException ex)
        {
            // Если сообщение нельзя отредактировать, отправляем новое
            LogError($"Не удалось отредактировать сообщение: {ex.Message}");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: threadId,
                text: profileMessage,
                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            LogError($"Ошибка отправки профиля: {ex.Message}");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: threadId,
                text: "❌ Не удалось загрузить профиль. Попробуйте еще раз.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // КОЛОДА: Текстовые команды управления
    // ============================

    private static async Task HandleDeckCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await ShowDeckInfo(bot, chatId, userId, messageThreadId, ct);
            return;
        }

        var subCommand = parts[1].ToLower();

        switch (subCommand)
        {
            case "info":
                await ShowDeckInfo(bot, chatId, userId, messageThreadId, ct);
                break;
            case "add":
                if (parts.Length < 3)
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: "❌ Использование: /deck add <название_карты>\n" +
                              "Пример: /deck add Огненный дракон\n" +
                              "Или: /deck add дракон (часть названия)\n\n" +
                              "Чтобы увидеть свои карты: /deck list",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }
                var cardNameToAdd = string.Join(" ", parts.Skip(2));
                await AddCardToDeckByName(bot, chatId, userId, cardNameToAdd, messageThreadId, ct);
                break;
            case "remove":
            case "delete":
            case "del":
                if (parts.Length < 3)
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: "❌ Использование: /deck remove <название_карты>\n" +
                              "Пример: /deck remove Огненный дракон\n" +
                              "Или: /deck remove дракон (часть названия)",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }
                var cardNameToRemove = string.Join(" ", parts.Skip(2));
                await RemoveCardFromDeckByName(bot, chatId, userId, cardNameToRemove, messageThreadId, ct);
                break;
            case "clear":
                await ClearDeck(bot, chatId, userId, messageThreadId, ct);
                break;
            case "list":
                await ShowCollectionForDeck(bot, chatId, userId, messageThreadId, ct);
                break;
            case "favorite":
                if (parts.Length < 3)
                {
                    await ShowFavoriteInfo(bot, chatId, userId, messageThreadId, ct);
                    return;
                }

                if (parts[2].ToLower() == "set")
                {
                    if (parts.Length < 4)
                    {
                        await bot.SendMessage(
                            chatId: chatId,
                            messageThreadId: messageThreadId,
                            text: "❌ Использование: /deck favorite set <название_карты>\n" +
                                  "Пример: /deck favorite set Огненный дракон\n\n" +
                                  "💡 <i>Карта ищется во всей вашей коллекции, не только в колоде!</i>",
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                        );
                        return;
                    }

                    var favoriteCardName = string.Join(" ", parts.Skip(3));

                    // Пробуем найти по номеру (например: "/deck favorite set дракон 1")
                    if (parts.Length >= 5 && int.TryParse(parts[parts.Length - 1], out int cardNumber) && cardNumber > 0)
                    {
                        // Убираем номер из поискового запроса
                        favoriteCardName = string.Join(" ", parts.Skip(3).Take(parts.Length - 4));
                        await SetFavoriteCardByNumber(bot, chatId, userId, favoriteCardName, cardNumber, messageThreadId, ct);
                    }
                    else
                    {
                        await SetFavoriteCardFromCollection(bot, chatId, userId, favoriteCardName, messageThreadId, ct);
                    }
                }
                else
                {
                    await ShowFavoriteInfo(bot, chatId, userId, messageThreadId, ct);
                }
                break;
            case "search":
                if (parts.Length < 3)
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: "❌ Использование: /deck search <название_или_часть>\n" +
                              "Пример: /deck search дракон",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }
                var searchTerm = string.Join(" ", parts.Skip(2));
                await SearchCardsInCollection(bot, chatId, userId, searchTerm, messageThreadId, ct);
                break;
            default:
                await ShowDeckHelp(bot, chatId, userId, messageThreadId, ct);
                break;
        }
    }

    private static async Task AddCardToDeckByName(ITelegramBotClient bot, long chatId, long userId, string cardNameSearch, int? messageThreadId, CancellationToken ct)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Поиск карты в коллекции пользователя
        var userCards = _userCards[userId].ToList();
        var matchingCards = new List<(string cardId, string cardName, string rarity)>();

        foreach (var cardId in userCards)
        {
            var cardName = GetCardName(cardId);
            if (cardName.Contains(cardNameSearch, StringComparison.OrdinalIgnoreCase))
            {
                var rarityInfo = GetCardRarityInfo(cardId);
                matchingCards.Add((cardId, cardName, rarityInfo.name));
            }
        }

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карт с названием '{cardNameSearch}' не найдено в вашей коллекции.\n" +
                      "Используйте команду /deck list чтобы увидеть все свои карты.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (matchingCards.Count > 1)
        {
            var message = $"🔍 Найдено несколько карт по запросу '{cardNameSearch}':\n\n";
            for (int i = 0; i < matchingCards.Count && i < 10; i++)
            {
                var card = matchingCards[i];
                message += $"{i + 1}. {GetRarityEmoji(card.rarity)} <b>{card.cardName}</b> ({card.rarity})\n";
            }

            if (matchingCards.Count > 10)
            {
                message += $"\n... и еще {matchingCards.Count - 10} карт\n";
            }

            message += "\nУточните название карты или используйте:\n";
            message += $"/deck add точное_название\n";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Найдена одна карта
        var foundCard = matchingCards[0];
        var cardIdToAdd = foundCard.cardId;

        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];

        if (deck.CardIds.Contains(cardIdToAdd))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карта '{foundCard.cardName}' уже в колоде!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (deck.CardIds.Count >= MAX_DECK_SIZE)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Колода полна! Максимум {MAX_DECK_SIZE} карт.\n" +
                      "Удалите карту командой /deck remove название",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        deck.CardIds.Add(cardIdToAdd);
        SaveUserDecks();

        var rarityEmoji = GetRarityEmoji(foundCard.rarity);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"✅ Карта добавлена в колоду!\n\n" +
                  $"{rarityEmoji} <b>{foundCard.cardName}</b> ({foundCard.rarity})\n" +
                  $"🎴 Карт в колоде: {deck.CardIds.Count}/{MAX_DECK_SIZE}",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task RemoveCardFromDeckByName(ITelegramBotClient bot, long chatId, long userId, string cardNameSearch, int? messageThreadId, CancellationToken ct)
    {
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];

        if (deck.CardIds.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Колода пуста!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Поиск карты в колоде
        var matchingCards = new List<(string cardId, string cardName, string rarity, int index)>();

        for (int i = 0; i < deck.CardIds.Count; i++)
        {
            var cardId = deck.CardIds[i];
            var cardName = GetCardName(cardId);
            if (cardName.Contains(cardNameSearch, StringComparison.OrdinalIgnoreCase))
            {
                var rarityInfo = GetCardRarityInfo(cardId);
                matchingCards.Add((cardId, cardName, rarityInfo.name, i));
            }
        }

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карт с названием '{cardNameSearch}' не найдено в колоде.\n" +
                      "Используйте команду /deck info чтобы увидеть колоду.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (matchingCards.Count > 1)
        {
            var message = $"🔍 Найдено несколько карт по запросу '{cardNameSearch}':\n\n";
            for (int i = 0; i < matchingCards.Count && i < 10; i++)
            {
                var card = matchingCards[i];
                message += $"{i + 1}. {GetRarityEmoji(card.rarity)} <b>{card.cardName}</b> ({card.rarity})\n";
            }

            message += "\nУточните название карты или используйте:\n";
            message += $"/deck remove точное_название\n";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Найдена одна карта
        var foundCard = matchingCards[0];

        // Если удаляем любимую карту, сбрасываем её
        if (deck.FavoriteCardId == foundCard.cardId)
        {
            deck.FavoriteCardId = "";
        }

        deck.CardIds.RemoveAt(foundCard.index);
        SaveUserDecks();

        var rarityEmoji = GetRarityEmoji(foundCard.rarity);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"🗑️ Карта удалена из колоды!\n\n" +
                  $"{rarityEmoji} <b>{foundCard.cardName}</b> ({foundCard.rarity})\n" +
                  $"🎴 Карт в колоде: {deck.CardIds.Count}/{MAX_DECK_SIZE}",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task SearchCardsInCollection(ITelegramBotClient bot, long chatId, long userId, string searchTerm, int? messageThreadId, CancellationToken ct)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var userCards = _userCards[userId].ToList();
        var deck = _userDecks.ContainsKey(userId) ? _userDecks[userId] : new UserDeck();
        var matchingCards = new List<(string cardId, string cardName, string rarity, bool inDeck, bool isFavorite)>();

        foreach (var cardId in userCards)
        {
            var cardName = GetCardName(cardId);
            if (cardName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                var rarityInfo = GetCardRarityInfo(cardId);
                var isInDeck = deck.CardIds.Contains(cardId);
                var isFavorite = deck.FavoriteCardId == cardId;
                matchingCards.Add((cardId, cardName, rarityInfo.name, isInDeck, isFavorite));
            }
        }

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"🔍 Карт по запросу '{searchTerm}' не найдено.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var message = $"🔍 Результаты поиска '{searchTerm}':\n";
        message += $"Найдено карт: {matchingCards.Count}\n\n";

        for (int i = 0; i < matchingCards.Count && i < 15; i++)
        {
            var card = matchingCards[i];
            var status = "";
            if (card.isFavorite) status += "❤️ ";
            if (card.inDeck) status += "✅ ";

            message += $"{status}{GetRarityEmoji(card.rarity)} <b>{card.cardName}</b> ({card.rarity})\n";
        }

        if (matchingCards.Count > 15)
        {
            message += $"\n... и еще {matchingCards.Count - 15} карт\n";
        }

        message += "\n<b>Команды:</b>\n";
        message += $"/deck add {searchTerm} - добавить найденные карты\n";
        message += $"/deck list - показать все карты";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
    // ============================

    private static string GetRarityEmoji(string rarityName)
    {
        return rarityName switch
        {
            "Обычная" => "🔵",
            "Редкая" => "🟠",
            "Эпическая" => "🟣",
            "Легендарная" => "⚪",
            "Чемпионская" => "🌟",
            "Эксклюзивная" => "💜",
            _ => "❓"
        };
    }

    // Обновленный метод ShowDeckInfo (изменения в тексте команд)
    private static async Task ShowDeckInfo(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];
        var cardCount = _userCards.ContainsKey(userId) ? _userCards[userId].Count : 0;

        var message = $"<b>🎴 МОЯ КОЛОДА</b>\n\n";

        if (deck.CardIds.Count == 0)
        {
            message += "Ваша колода пуста. Добавьте карты из вашей коллекции!\n\n";
        }
        else
        {
            message += $"<b>Карты в колоде ({deck.CardIds.Count}/{MAX_DECK_SIZE}):</b>\n";

            for (int i = 0; i < deck.CardIds.Count; i++)
            {
                var cardId = deck.CardIds[i];
                var cardName = GetCardName(cardId);
                var rarityInfo = GetCardRarityInfo(cardId);

                message += $"{i + 1}. {rarityInfo.emoji} <b>{cardName}</b> ({rarityInfo.name})\n";
            }
            message += "\n";
        }

        message += $"<b>📊 Статистика:</b>\n";
        message += $"• Карт в коллекции: <b>{cardCount}</b>\n";
        message += $"• Карт в колоде: <b>{deck.CardIds.Count}/{MAX_DECK_SIZE}</b>\n\n";

        if (!string.IsNullOrEmpty(deck.FavoriteCardId))
        {
            var favoriteCardName = GetCardName(deck.FavoriteCardId);
            var favoriteCardRarity = GetCardRarityInfo(deck.FavoriteCardId);
            message += $"• ❤️ Любимая карта: {favoriteCardRarity.emoji} <b>{favoriteCardName}</b>\n";
        }

        message += "\n<b>📋 Команды управления колодой:</b>\n";
        message += "/deck info - показать эту информацию\n";
        message += "/deck list - показать коллекцию\n";
        message += "/deck search название - поиск карт\n";
        message += "/deck add название - добавить карту\n";
        message += "/deck remove название - удалить карту\n";
        message += "/deck clear - очистить колоду\n";
        message += "/deck favorite - показать любимую карту\n";
        message += "/deck favorite set название - установить любимую карту\n";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // Обновленный метод ShowDeckHelp
    private static async Task ShowDeckHelp(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var message = MessageManager.Get("deck_help");

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task ShowCollectionForDeck(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var userCards = _userCards[userId].ToList();
        var deck = _userDecks.ContainsKey(userId) ? _userDecks[userId] : new UserDeck();

        var message = $"<b>🎴 ВАША КОЛЛЕКЦИЯ</b>\n\n";
        message += $"Всего карт: <b>{userCards.Count}</b>\n";
        message += $"Мест в колоде: <b>{deck.CardIds.Count}/{MAX_DECK_SIZE}</b>\n";
        message += $"❤️ Любимая карта: {(string.IsNullOrEmpty(deck.FavoriteCardId) ? "не выбрана" : GetCardName(deck.FavoriteCardId))}\n\n";

        // Показываем первые 20 карт с номерами
        for (int i = 0; i < userCards.Count && i < 20; i++)
        {
            var cardId = userCards[i];
            var cardName = GetCardName(cardId);
            var rarityInfo = GetCardRarityInfo(cardId);
            var isInDeck = deck.CardIds.Contains(cardId);
            var isFavorite = deck.FavoriteCardId == cardId;

            var status = "";
            if (isFavorite) status = "❤️ ";
            if (isInDeck) status += "✅ ";

            message += $"<b>{i + 1}.</b> {status}{rarityInfo.emoji} <b>{cardName}</b> ({rarityInfo.name})\n";
        }

        if (userCards.Count > 20)
        {
            message += $"\n... и ещё {userCards.Count - 20} карт\n";
        }

        message += "\n<b>📝 Чтобы добавить карту в колоду:</b>\n";
        message += "Используйте команду <code>/deck add название</code>\n";
        message += "Например: <code>/deck add Огненный дракон</code>\n\n";

        message += "<b>❤️ Чтобы установить любимую карту:</b>\n";
        message += "<code>/deck favorite set название</code>\n";
        message += "Например: <code>/deck favorite set дракон</code>\n\n";

        message += "<b>🔍 Если найдено несколько карт:</b>\n";
        message += "<code>/deck search дракон</code> - увидеть все варианты\n";
        message += "<code>/deck favorite set дракон 1</code> - выбрать первый вариант";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }


    private static async Task ClearDeck(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];

        if (deck.CardIds.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Колода уже пуста!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var removedCount = deck.CardIds.Count;
        deck.CardIds.Clear();
        deck.FavoriteCardId = "";
        SaveUserDecks();

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"🗑️ Колода очищена!\n\n" +
                  $"✅ Удалено карт: <b>{removedCount}</b>\n" +
                  $"🎴 Карт в колоде: 0/{MAX_DECK_SIZE}\n" +
                  $"❤️ Любимая карта: сброшена",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task ShowFavoriteInfo(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];

        if (string.IsNullOrEmpty(deck.FavoriteCardId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❤️ <b>ЛЮБИМАЯ КАРТА</b>\n\n" +
                      "У вас не установлена любимая карта.\n\n" +
                      "💡 <b>Как установить любимую карту:</b>\n" +
                      "1. Найдите карту в своей коллекции:\n" +
                      "   <code>/deck search название</code>\n" +
                      "2. Установите её как любимую:\n" +
                      "   <code>/deck favorite set название</code>\n\n" +
                      "📋 <b>Примеры:</b>\n" +
                      "• <code>/deck search дракон</code> - найти все карты с 'дракон'\n" +
                      "• <code>/deck favorite set Огненный дракон</code>\n" +
                      "• <code>/deck favorite set дракон 1</code> - выбрать первый вариант",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var cardId = deck.FavoriteCardId;
        var cardName = GetCardName(cardId);
        var rarityInfo = GetCardRarityInfo(cardId);

        // Проверяем, есть ли карта в коллекции
        bool hasCardInCollection = _userCards.ContainsKey(userId) && _userCards[userId].Contains(cardId);
        bool isInDeck = deck.CardIds.Contains(cardId);

        var message = $"❤️ <b>ВАША ЛЮБИМАЯ КАРТА</b>\n\n" +
                     $"{rarityInfo.emoji} <b>{cardName}</b> ({rarityInfo.name})\n" +
                     $"💪 Вес карты: <b>{rarityInfo.weight}</b>\n\n";

        if (!hasCardInCollection)
        {
            message += "⚠️ <i>Эта карта больше не в вашей коллекции</i>\n";
        }
        else if (isInDeck)
        {
            var positionInDeck = deck.CardIds.IndexOf(cardId) + 1;
            message += $"🎴 В колоде: <b>#{positionInDeck}</b>\n";
        }
        else
        {
            message += "📦 Только в коллекции (не в колоде)\n";
        }

        message += "\n<b>📋 Команды для работы:</b>\n";

        if (hasCardInCollection && !isInDeck)
        {
            message += $"/deck add \"{cardName}\" - добавить в колоду\n";
        }
        else if (isInDeck)
        {
            var positionInDeck = deck.CardIds.IndexOf(cardId) + 1;
            message += $"/deck remove {positionInDeck} - удалить из колоды\n";
        }

        message += $"/deck search {cardName} - найти похожие карты\n";
        message += $"/deck favorite set новая_карта - изменить любимую карту";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }



    // ============================
    // УТИЛИТЫ ДЛЯ КОЛОД
    // ============================

    private static string GetCardName(string cardId)
    {
        var parts = cardId.Split(':');
        if (parts.Length < 2) return "Неизвестная карта";

        var fileName = parts[1];
        return Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ');
    }

    private static (string emoji, string name, int weight) GetCardRarityInfo(string cardId)
    {
        var parts = cardId.Split(':');
        if (parts.Length < 1) return ("❓", "Неизвестно", 0);

        var rarity = parts[0];
        if (_rarities.ContainsKey(rarity))
        {
            var r = _rarities[rarity];
            return (r.emoji, r.name, r.weight);
        }

        return ("❓", "Неизвестно", 0);
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Маршрутизация команд
    // ============================


    private static async Task RouteCommand(ITelegramBotClient bot, string command, long chatId, long userId, Message message, CancellationToken ct)
    {
        var messageThreadId = message.MessageThreadId;

        // Если это группа и есть настройки топика, проверяем
        if (chatId < 0)
        {
            var topicId = GetTopicIdForGroup(chatId);
            if (topicId.HasValue)
            {
                // Если сообщение из неправильного топика, отправляем ответ в нужный топик
                if (messageThreadId != topicId.Value)
                {
                    try
                    {
                        await bot.SendMessage(
                            chatId: chatId,
                            messageThreadId: messageThreadId,
                            text: $"⚠️ Бот настроен на работу только в топике #{topicId.Value}.\n" +
                                  $"Используйте бота в указанном топике или в личных сообщениях.",
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                        );
                    }
                    catch { }
                    return;
                }
            }
        }

        // Обработка админского меню
        if (command == "admin_menu")
        {
            if (!IsAdmin(userId))
            {
                return;
            }

            await SendAdminMenu(bot, chatId, userId, messageThreadId, ct);
            return;
        }

        // Обычные команды
        switch (command)
        {
            case "start":
                await SendStartMessage(bot, chatId, messageThreadId, ct);
                break;
            case "help":
                await SendHelpMenu(bot, chatId, userId, message.MessageThreadId, null, ct);
                break;
            case "pic":
                await OpenRandomCard(bot, chatId, userId, messageThreadId, ct);
                break;
            case "top":
                await ShowTopMenu(bot, chatId, userId, messageThreadId, ct);
                break;
            case "top_money":
                await SendLeaderboardByMoney(bot, chatId, userId, messageThreadId, ct);
                break;
            case "top_card":
                await SendLeaderboardByCards(bot, chatId, userId, messageThreadId, ct);
                break;
            case "gold":
            case "balance":
                await SendUserGold(bot, chatId, userId, messageThreadId, ct);
                break;
            case "stats":
                await SendUserStats(bot, chatId, userId, messageThreadId, ct);
                break;
            case "mycards":
                await SendUserCards(bot, chatId, userId, messageThreadId, ct);
                break;
            case "mycards_menu":
                await SendUserCards(bot, chatId, userId, messageThreadId, ct);
                break;
            case "chance":
            case "prob":
                await SendChanceInfo(bot, chatId, messageThreadId, ct);
                break;
            case "cooldown":
            case "cd":
                await SendCooldownInfo(bot, chatId, userId, messageThreadId, ct);
                break;
            case "profile":
            case "профиль":
                await SendUserProfile(bot, chatId, userId, messageThreadId, ct);
                break;
            case "deck":
            case "колода":
                var parts = (message.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                await HandleDeckCommand(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "promo":
                var promoParts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                await HandlePromoCommand(bot, chatId, userId, promoParts, messageThreadId, ct);
                break;
            case "shop":
                await SendShopMenu(bot, chatId, userId, messageThreadId, ct);
                break;
            case "buy":
                var buyParts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                await HandleBuyCommand(bot, chatId, userId, buyParts, messageThreadId, ct);
                break;
            case "convert":
                var convertParts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                await HandleConvertCommand(bot, chatId, userId, convertParts, messageThreadId, ct);
                break;
            case "gems":
                await SendUserGemsBalance(bot, chatId, userId, messageThreadId, ct);
                break;
            case "chestlimit":
            case "limit":
            case "лимит":
                await SendChestLimitInfo(bot, chatId, userId, messageThreadId, ct);
                break;
            case "cards":                    // ДОЛЖНО БЫТЬ
                var cardsParts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                await HandleCardsCommand(bot, chatId, userId, cardsParts, messageThreadId, ct);
                break;
            case "view":                      // ДОЛЖНО БЫТЬ
            case "showcard":                  // ДОЛЖНО БЫТЬ
                var viewParts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                await HandleViewCardCommand(bot, chatId, userId, viewParts, messageThreadId, ct);
                break;
            case "evolutions":
            case "myevolutions":
            case "evolution":
            case "эволюции":
            case "моиэволюции":
                await SendUserEvolutions(bot, chatId, userId, messageThreadId, ct);
                break;
            default:
                break;
        }
    }


    // ============================
    // ПРОФИЛЬ: Обновленный метод с учетом системы колод
    // ============================

    private static async Task SendUserProfile(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (!_users.ContainsKey(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Ваш профиль не найден. Начните с команды /start",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var user = _users[userId];
        var userGems = _userGems.ContainsKey(userId) ? _userGems[userId] : 0;
        var cardCount = _userCards.ContainsKey(userId) ? _userCards[userId].Count : 0;
        var isAdmin = IsAdmin(userId);
        var adminLevel = GetAdminLevel(userId);
        var totalPossibleCards = 0;
        foreach (var rarity in _imageFilesCache.Keys)
        {
            totalPossibleCards += _imageFilesCache[rarity].Length;
        }

        var deck = _userDecks.ContainsKey(userId) ? _userDecks[userId] : new UserDeck();
        var deckSize = deck.CardIds.Count;

        var collectionPercentage = totalPossibleCards > 0
            ? Math.Round((double)cardCount / totalPossibleCards * 100, 1)
            : 0;

        string favoriteCardInfo = "";
        if (!string.IsNullOrEmpty(deck.FavoriteCardId))
        {
            var favoriteCardName = GetCardName(deck.FavoriteCardId);
            var favoriteCardRarity = GetCardRarityInfo(deck.FavoriteCardId);
            favoriteCardInfo = $"{favoriteCardRarity.emoji} <b>{favoriteCardName}</b>";
        }
        else
        {
            favoriteCardInfo = "Не выбрана";
        }

        var profileMessage = MessageManager.Get("profile",
    user.FirstName + " " + user.LastName,
    string.IsNullOrEmpty(user.Username) ? "не установлен" : "@" + user.Username,
    user.UserId,
    user.Gold,
    userGems,
    user.Gold + CalculateTotalGoldSpent(userId),
    cardCount,
    totalPossibleCards,
    collectionPercentage,
    user.TotalCards,
    deckSize,
    MAX_DECK_SIZE,
    favoriteCardInfo,
    user.CommonCards,
    user.RareCards,
    user.EpicCards,
    user.LegendaryCards,
    user.ChampionCards,
    isAdmin ? $"Да ({adminLevel})" : "Нет",
    user.Registered.ToString("dd.MM.yyyy"),
    user.LastCardTime == DateTime.MinValue ? "никогда" : user.LastCardTime.ToString("dd.MM.yyyy HH:mm")
);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: profileMessage,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Работа с пользователями
    // ============================

    private static void EnsureUserExists(long userId, User user)
    {
        if (!_users.ContainsKey(userId))
        {
            lock (_fileLock)
            {
                if (!_users.ContainsKey(userId))
                {
                    _users[userId] = new UserData
                    {
                        UserId = userId,
                        Username = user.Username ?? "",
                        FirstName = user.FirstName ?? "",
                        LastName = user.LastName ?? "",
                        Gold = 0,
                        Gems = 0, // Инициализируем гемы
                        TotalCards = 0,
                        Registered = DateTime.UtcNow,
                        LastCardTime = DateTime.MinValue
                    };
                    SaveUsers();

                    // Инициализируем гемы в отдельном словаре
                    _userGems[userId] = 0;
                    SaveUserGems();

                    // Создаем пустую колоду для нового пользователя
                    if (!_userDecks.ContainsKey(userId))
                    {
                        _userDecks[userId] = new UserDeck
                        {
                            UserId = userId,
                            CardIds = new List<string>(),
                            FavoriteCardId = ""
                        };
                        SaveUserDecks();
                    }

                    LogInfo($"Новый пользователь: {userId} (@{user.Username})");
                }
            }
        }
    }

    private static void LoadUsers()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_usersFile))
                {
                    var json = File.ReadAllText(_usersFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, UserData>>(json)
                               ?? new Dictionary<long, UserData>();
                    _users = new ConcurrentDictionary<long, UserData>(tempDict);
                }
                else
                {
                    _users = new ConcurrentDictionary<long, UserData>();
                }
            }
            LogInfo($"Загружено {_users.Count} пользователей");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки users.json: {ex.Message}");
            _users = new ConcurrentDictionary<long, UserData>();
        }
    }

    private static void SaveUsers()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _users.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_usersFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения users.json: {ex.Message}");
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Обработка ошибок
    // ============================

    private static Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiEx => $"Telegram API Error [{apiEx.ErrorCode}]: {apiEx.Message}",
            _ => $"Ошибка: {exception.Message}"
        };

        LogError(errorMessage);

        return Task.CompletedTask;
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Стартовое сообщение
    // ============================

    private static async Task SendStartMessage(ITelegramBotClient bot, long chatId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: убрана кнопка группы, оставлен только канал
        var keyboard = new InlineKeyboardMarkup(new[]
        {
        new[]
        {
            InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "check_membership")
        }
    });

        var message = MessageManager.Get("start", COOLDOWN_SECONDS / 60);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Открытие карт (/card, /pic)
    // ============================

    private static async Task OpenRandomCard(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                new[]
                {
                    InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "check_membership")
                }
                }),
                cancellationToken: ct
            );
            return;
        }

        LogInfo($"Запрос карты от пользователя: {userId}");

        var user = _users[userId];

        // Проверка кулдауна
        if (user.LastCardTime != DateTime.MinValue)
        {
            var timeSinceLastCard = DateTime.UtcNow - user.LastCardTime;
            var remainingCooldown = COOLDOWN_SECONDS - timeSinceLastCard.TotalSeconds;

            if (remainingCooldown > 0)
            {
                var remainingMinutes = Math.Ceiling(remainingCooldown / 60);
                var remainingSeconds = Math.Ceiling(remainingCooldown % 60);

                var cooldownMessages = MessageManager.GetArray("cooldown_messages");
                var randomMessage = string.Format(
                    cooldownMessages[_random.Next(cooldownMessages.Length)],
                    remainingMinutes,
                    remainingSeconds
                );

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"{randomMessage}",
                    cancellationToken: ct
                );
                return;
            }
        }

        var rarity = GetRandomRarity();
        LogInfo($"Выбрана редкость: {rarity} для пользователя {userId}");

        if (!_imageFilesCache.ContainsKey(rarity) || _imageFilesCache[rarity].Length == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("no_cards", rarity),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var files = _imageFilesCache[rarity];
        var randomFile = files[_random.Next(files.Length)];

        LogInfo($"Выбран файл: {Path.GetFileName(randomFile)}");

        var fileName = Path.GetFileName(randomFile);
        var cardName = Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ');

        var reward = _rarities[rarity];
        var isDuplicate = false;
        int goldEarned = 0;

        var cardId = $"{rarity}:{fileName}";

        if (!_userCards.ContainsKey(userId))
        {
            _userCards[userId] = new HashSet<string>();
        }

        lock (_fileLock)
        {
            if (_userCards[userId].Contains(cardId))
            {
                isDuplicate = true;

                // Проверяем, есть ли эволюция для этой карты
                if (HasEvolutionForCard(userId, cardName))
                {
                    // Если есть эволюция, даем в 2 раза больше золота
                    goldEarned = reward.gold / 4 * 2; // 25% от стоимости * 2 = 50%
                }
                else
                {
                    goldEarned = reward.gold / 4;
                }

                user.Gold += goldEarned;
            }
            else
            {
                _userCards[userId].Add(cardId);
                goldEarned = reward.gold;
                user.Gold += reward.gold;
            }

            user.TotalCards++;

            switch (rarity)
            {
                case "common": user.CommonCards++; break;
                case "rare": user.RareCards++; break;
                case "epic": user.EpicCards++; break;
                case "legendary": user.LegendaryCards++; break;
                case "champion": user.ChampionCards++; break;
            }

            user.LastCardTime = DateTime.UtcNow;

            _users[userId] = user;
            SaveUsers();
            SaveUserCards();
        }

        // Проверяем эволюции ВНЕ блока lock
        await CheckAndActivateEvolutions(bot, userId, cardName, messageThreadId, ct);

        FileStream stream = null;
        try
        {
            var fileInfo = new FileInfo(randomFile);
            if (fileInfo.Length > MAX_FILE_SIZE_MB * 1024 * 1024)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: MessageManager.Get("file_too_large",
                        fileInfo.Length / 1024 / 1024,
                        MAX_FILE_SIZE_MB),
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            stream = File.OpenRead(randomFile);

            var userNameDisplay = !string.IsNullOrEmpty(user.Username)
                ? $"@{user.Username}"
                : $"Игрок {user.UserId}";

            var dropMessages = GetDropMessages(rarity);
            var dropMessage = dropMessages[_random.Next(dropMessages.Length)];

            string caption;

            if (isDuplicate)
            {
                int duplicateGold = goldEarned;
                var duplicateMessages = GetDuplicateMessages(rarity);
                var duplicateMessage = duplicateMessages[_random.Next(duplicateMessages.Length)];

                caption = $"{userNameDisplay}\n"
                        + $"【{reward.emoji}】• {dropMessage} ({cardName})\n"
                        + $"{duplicateMessage}\n"
                        + $"💰 +{duplicateGold} золота ({(HasEvolutionForCard(userId, cardName) ? "эволюция x2" : "25%")})";
            }
            else
            {
                var newCardMessages = GetNewCardMessages(rarity);
                var newCardMessage = newCardMessages[_random.Next(newCardMessages.Length)];

                caption = $"{userNameDisplay}\n"
                        + $"【{reward.emoji}】• {dropMessage} ({cardName})\n"
                        + $"{newCardMessage}\n"
                        + $"💰 +{reward.gold} золота";
            }

            await bot.SendPhoto(
                chatId: chatId,
                messageThreadId: messageThreadId,
                photo: new InputFileStream(stream, fileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogInfo($"Карта отправлена пользователю {userId}. Дубликат: {isDuplicate}, Золото: {user.Gold}");
        }
        catch (FileNotFoundException)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("file_not_found"),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            CacheImageFiles();
        }
        catch (IOException)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("io_error"),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("send_error"),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static string[] GetDropMessages(string rarity)
    {
        var commonMessages = new[]
        {
            "Карта выпала!",
            "Новый дроп!",
            "Вы получили карту!",
            "Карта добавлена!",
            "Свиток открыт!",
            "Находка в колоде!",
            "Получена карта!",
            "Карта из колоды!"
        };

        var rareMessages = new[]
        {
            "Редкая удача!",
            "Ценная находка!",
            "Сила возрастает!",
            "Точно в цель!",
            "Редкий дроп!",
            "Мощная карта!",
            "Необычная удача!",
            "Особый экземпляр!"
        };

        var epicMessages = new[]
        {
            "Эпическая мощь!",
            "Магический дроп!",
            "Великолепная сила!",
            "Невероятная удача!",
            "Шторм начинается!",
            "Магия явлена!",
            "Запредельная мощь!",
            "Эпическое везение!"
        };

        var legendaryMessages = new[]
        {
            "Легенда явлена!",
            "Мифический дроп!",
            "Легендарная мощь!",
            "Карта легенд!",
            "Великая находка!",
            "Сияние мощи!",
            "Король карт прибыл!",
            "Легендарное везение!"
        };

        var championMessages = new[]
        {
            "ЧЕМПИОНСКИЙ ДРОП!",
            "ВЕРХОВНЫЙ ВОИТЕЛЬ!",
            "БОЖЕСТВЕННАЯ СИЛА!",
            "АБСОЛЮТНАЯ МОЩЬ!",
            "ВЕЛИЧАЙШАЯ УДАЧА!",
            "ЗАПРЕДЕЛЬНАЯ СИЛА!",
            "ВЕРШИНА МАСТЕРСТВА!",
            "ЧЕМПИОН ЯВИЛСЯ!"
        };

        return rarity switch
        {
            "common" => commonMessages,
            "rare" => rareMessages,
            "epic" => epicMessages,
            "legendary" => legendaryMessages,
            "champion" => championMessages,
            _ => commonMessages
        };
    }

    private static string[] GetNewCardMessages(string rarity)
    {
        return rarity switch
        {
            "common" => new[]
            {
                "✨ Новичкам везёт! Обычная удача!",
                "🔵 Неплохо для начала тренировок!",
                "⚡ Базовая подготовка завершена!"
            },
            "rare" => new[]
            {
                "🟠 Редкая удача улыбнулась вам!",
                "🌟 Не каждому так везёт с редкими!",
                "💎 Настоящая жемчужина в коллекции!"
            },
            "epic" => new[]
            {
                "🟣 Эпический успех на поле боя!",
                "🌀 Великолепная магическая мощь!",
                "💫 Невероятная сила в ваших руках!"
            },
            "legendary" => new[]
            {
                "⚪ Легенда стала реальностью!",
                "👑 Миф ожил в вашей колоде!",
                "⭐ Легендарная мощь присоединилась!"
            },
            "champion" => new[]
            {
                "🏆 ЧЕМПИОНСКИЙ ДРОП! ВЕРШИНА МАСТЕРСТВА!",
                "👑 НЕПРЕВЗОЙДЁННАЯ МОЩЬ В КОЛОДЕ!",
                "🌟 АБСОЛЮТНАЯ СИЛА ПРИБЫЛА НА ПОМОЩЬ!"
            },
            _ => new[] { "Новая карта в коллекции!" }
        };
    }

    private static string[] GetDuplicateMessages(string rarity)
    {
        return rarity switch
        {
            "common" => new[]
            {
                "🔵 Уже была... но золото лишним не бывает!",
                "⚡ Снова этот боец... опыт дорогого стоит!",
                "📦 Знакомое лицо в колоде!"
            },
            "rare" => new[]
            {
                "🟠 Редкая, но уже знакомая...",
                "🌟 Снова эта редкость... коллекция растёт!",
                "💎 Уже собирается комплект!"
            },
            "epic" => new[]
            {
                "🟣 Эпический дубль! Магия не помешает!",
                "🌀 Снова эта мощь... вдвойне эффективней!",
                "💫 Знакомая энергия, но всё равно круто!"
            },
            "legendary" => new[]
            {
                "⚪ Легендарный дубль! Слава приумножается!",
                "👑 Дважды легенда - двойная гордость!",
                "⭐ Снова эта легенда... почётно!"
            },
            "champion" => new[]
            {
                "🏆 ПОВТОР ЧЕМПИОНА! СИЛА УДВАИВАЕТСЯ!",
                "👑 ДВА ЧЕМПИОНА - ЭТО УЖЕ АРМИЯ!",
                "🌟 БОЖЕСТВЕННАЯ МОЩЬ ПОВТОРИЛАСЬ!"
            },
            _ => new[] { "Карта уже была в коллекции!" }
        };
    }

    // ============================
    // АДМИН КОМАНДЫ: Обработчики
    // ============================

    private static async Task SendAdminMenu(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет прав доступа к админ-панели.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var level = GetAdminLevel(userId);
        var isSuperAdmin = IsSuperAdmin(userId);
        var isOwner = IsOwner(userId);

        var message = "👮 <b>АДМИН-ПАНЕЛЬ</b>\n\n";

        message += $"🆔 <b>Ваш ID:</b> <code>{userId}</code>\n";
        message += $"📊 <b>Уровень:</b> {level}\n";
        message += $"👥 <b>Пользователей в боте:</b> {_users.Count}\n\n";

        // Простое меню без кнопок
        message += "<b>Доступные команды:</b>\n";
        message += "/ahelp - показать справку по командам\n";
        message += "/admins - список администраторов\n";
        message += "/addgold - добавить золото пользователю\n";
        message += "/removegold - удалить золото у пользователя\n";
        message += "/addcard - добавить карту пользователю\n";
        message += "/resetcd - сбросить кулдаун пользователю\n";
        message += "/statsall - общая статистика\n";
        message += "/broadcast - сделать рассылку\n";

        if (isSuperAdmin || isOwner)
        {
            message += "\n<b>Команды для суперадмина:</b>\n";
            message += "/addadmin - добавить администратора\n";
            message += "/removeadmin - удалить администратора\n";
            message += "/setadminlevel - изменить уровень админа\n";
            message += "/topicset - установить топик для бота\n";
            message += "/topicclear - очистить настройки топика\n";
            message += "/topicinfo - информация о топиках\n";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task SendAdminHelpMessage(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var level = GetAdminLevel(userId);
        var isSuperAdmin = IsSuperAdmin(userId);
        var isOwner = IsOwner(userId);

        var message = "👮 <b>АДМИН-ПАНЕЛЬ</b>\n\n";

        message += "<b>Основные команды:</b>\n";
        message += "/admin или /ahelp - эта справка\n";
        message += "/admins - список администраторов\n";

        if (isSuperAdmin || isOwner)
        {
            message += "\n<b>Управление администраторами:</b>\n";
            message += "/addadmin ID или @user [level] - добавить админа\n";
            message += "/removeadmin ID или @user - удалить админа\n";
            message += "/setadminlevel ID или @user level - изменить уровень\n";

            message += "\n<b>Управление топиками:</b>\n";
            message += "/topicset [topic_id] - установить топик для бота\n";
            message += "/topicclear - очистить настройки топика\n";
            message += "/topicinfo - информация о топиках\n";

            message += "\n<b>Управление эволюциями:</b>\n";
            message += "/listevolutions - список всех эволюций\n";
            message += "/addevolution - создать эволюцию (в разработке)\n";
            message += "/removeevolution название - удалить эволюцию\n";
            message += "/resetevolutions ID или @user - сбросить эволюции пользователя\n";
        }

        message += "\n<b>Управление пользователями:</b>\n";
        message += "/addgold ID или @user amount - добавить золото\n";
        message += "/removegold ID или @user amount - удалить золото\n";
        message += "/setgold ID или @user amount - установить золото\n";
        message += "/addcard ID или @user rarity name - добавить карту\n";
        message += "/removecard ID или @user card_id - удалить карту\n";
        message += "/resetcd ID или @user - сбросить кулдаун\n";
        message += "/resetstats ID или @user - сбросить статистику\n";
        message += "/deleteuser ID или @user - удалить пользователя\n";

        // Добавляем раздел про промокоды только для владельца
        if (isOwner)
        {
            message += "\n<b>Управление промокодами (только владелец):</b>\n";
            message += "/addpromo КОД золото использований [карта rarity:filename] [дни N]\n";
            message += "/removepromo КОД - удалить промокод\n";
            message += "/listpromos - список промокодов\n";
        }

        message += "\n<b>Информационные команды:</b>\n";
        message += "/statsall - общая статистика\n";
        message += "/finduser name - найти пользователя\n";
        message += "/userinfo ID или @user - информация о пользователе\n";

        message += "\n<b>Управление каналом:</b>\n";
        message += "/sendtochannel text - отправить сообщение в канал\n";
        message += "/sendphoto rarity - отправить случайную карту в канал\n";
        message += "/pinmessage message_id - закрепить сообщение в канале\n";
        message += "/unpinmessage - открепить сообщение в канале\n";

        message += $"\n<b>Ваш уровень:</b> {level}\n";
        message += $"<b>Всего пользователей:</b> {_users.Count}\n";
        message += $"<b>Всего эволюций в игре:</b> {_evolutions.Count}\n";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task SendAdminList(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет прав для просмотра этого списка.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var adminList = GetAdminList();

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: adminList,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Админская маршрутизация (работает везде)
    // ============================

    private static async Task RouteAdminCommand(ITelegramBotClient bot, string command, long chatId, long userId, Message message, CancellationToken ct)
    {
        var messageThreadId = message.MessageThreadId;
        var text = message.Text ?? "";
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (command)
        {
            case "admin":
            case "ahelp":
                await SendAdminHelpMessage(bot, chatId, userId, messageThreadId, ct);
                break;
            case "admins":
                await SendAdminList(bot, chatId, userId, messageThreadId, ct);
                break;
            case "addadmin":
                await HandleAddAdmin(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "removeadmin":
                await HandleRemoveAdmin(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "setadminlevel":
                await HandleSetAdminLevel(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "addgold":
                await HandleAddGold(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "removegold":
                await HandleRemoveGold(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "setgold":
                await HandleSetGold(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "addcard":
                await HandleAddCard(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "removecard":
                await HandleRemoveCard(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "resetcd":
                await HandleResetCooldown(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "resetstats":
                await HandleResetStats(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "deleteuser":
                await HandleDeleteUser(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "broadcast":
                await HandleBroadcast(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "statsall":
                await HandleStatsAll(bot, chatId, userId, messageThreadId, ct);
                break;
            case "finduser":
                await HandleFindUser(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "userinfo":
                await HandleUserInfo(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "sendtochannel":
                await HandleSendToChannel(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "sendphoto":
                await HandleSendPhotoToChannel(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "pinmessage":
                await HandlePinMessage(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "unpinmessage":
                await HandleUnpinMessage(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "addpromo":
                await HandleAddPromo(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "removepromo":
                await HandleRemovePromo(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "listpromos":
                await HandleListPromos(bot, chatId, userId, messageThreadId, ct);
                break;
            case "topicset":
                await HandleTopicSet(bot, chatId, userId, parts, message, messageThreadId, ct);
                break;
            case "topicclear":
                await HandleTopicClear(bot, chatId, userId, messageThreadId, ct);
                break;
            case "topicinfo":
                await HandleTopicInfo(bot, chatId, userId, messageThreadId, ct);
                break;
            case "addevolution":
                await HandleAddEvolution(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "removeevolution":
                await HandleRemoveEvolution(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "resetevolutions":
                await HandleResetEvolutions(bot, chatId, userId, parts, messageThreadId, ct);
                break;
            case "listevolutions":
                await HandleListEvolutions(bot, chatId, userId, messageThreadId, ct);
                break;
            case "clearcache":
                ClearSubscriptionCache();
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "✅ Кэш подписок полностью очищен",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                break;

            case "clearcacheuser":
                if (parts.Length < 2)
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: "❌ Использование: /clearcacheuser <user_id>",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    break;
                }
                if (long.TryParse(parts[1], out long targetUserId))
                {
                    ClearSubscriptionCache(targetUserId);
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: $"✅ Кэш подписки для пользователя {targetUserId} очищен",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                }
                break;
            default:
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неизвестная админ-команда.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                break;
        }
    }

    // ============================
    // НОВЫЕ МЕТОДЫ: Управление топиками
    // ============================

    private static async Task HandleTopicSet(ITelegramBotClient bot, long chatId, long userId, string[] parts, Message message, int? messageThreadId, CancellationToken ct)
    {
        if (!IsSuperAdmin(userId) && !IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только суперадмины и владелец могут устанавливать топики",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверяем, что команда вызвана в группе
        if (chatId > 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Эту команду можно использовать только в группах",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        int topicId;
        if (parts.Length < 2)
        {
            // Если ID топика не указан, используем ID текущего топика
            if (message.MessageThreadId.HasValue)
            {
                topicId = message.MessageThreadId.Value;
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Укажите ID топика или вызовите команду из нужного топика\n\n" +
                          "Использование: /topicset [topic_id]\n" +
                          "Пример: /topicset 10 - установить топик с ID 10\n" +
                          "Или просто /topicset в нужном топике",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }
        }
        else
        {
            if (!int.TryParse(parts[1], out topicId))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверный ID топика. Укажите число",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }
        }

        var success = await SetTopicForGroup(chatId, topicId, userId);

        if (success)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Топик для бота установлен!\n\n" +
                      $"📌 Группа: <code>{chatId}</code>\n" +
                      $"🗂️ Топик: <code>{topicId}</code>\n\n" +
                      $"Теперь бот будет работать только в этом топике.\n" +
                      $"Обычные пользователи могут использовать бота в личных сообщениях.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Не удалось установить топик",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleTopicClear(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!IsSuperAdmin(userId) && !IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только суперадмины и владелец могут очищать настройки топиков",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверяем, что команда вызвана в группе
        if (chatId > 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Эту команду можно использовать только в группах",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var success = await ClearTopicForGroup(chatId, userId);

        if (success)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Настройки топика очищены!\n\n" +
                      $"📌 Группа: <code>{chatId}</code>\n\n" +
                      $"Теперь бот будет работать во всех топиках этой группы.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"⚠️ Настройки топика для этой группы не были установлены",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleTopicInfo(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только администраторы могут просматривать информацию о топиках",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var message = "🗂️ <b>ИНФОРМАЦИЯ О ТОПИКАХ</b>\n\n";

        if (_topicSettings.Count == 0)
        {
            message += "📭 Настройки топиков не установлены\n";
            message += "Бот работает во всех топиках групп\n";
        }
        else
        {
            message += "<b>Установленные топики:</b>\n\n";

            foreach (var setting in _topicSettings)
            {
                var groupId = setting.Key;
                var topicId = setting.Value;

                message += $"📌 Группа: <code>{groupId}</code>\n";
                message += $"🗂️ Топик: <code>{topicId}</code>\n\n";
            }

            message += $"<b>Всего настроек:</b> {_topicSettings.Count}\n";
        }

        message += $"\n<b>📋 Команды управления топиками:</b>\n";
        message += "/topicset [topic_id] - установить топик\n";
        message += "/topicclear - очистить настройки\n";
        message += "/topicinfo - эта информация\n\n";

        message += "<b>ℹ️ Примечание:</b>\n";
        message += "• В личных сообщениях бот работает всегда\n";
        message += "• В группах работает только в указанных топиках\n";
        message += "• Если топик не указан - работает везде";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // АДМИН КОМАНДЫ: Обработчики
    // ============================

    private static async Task HandleAddAdmin(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /addadmin <code>ID</code> или @username [level=admin]\n\n" +
                      "Уровни:\n" +
                      "• admin - обычный администратор\n" +
                      "• super - суперадминистратор",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var level = AdminLevel.Admin;
            if (parts.Length >= 3)
            {
                level = parts[2].ToLower() switch
                {
                    "super" => AdminLevel.SuperAdmin,
                    _ => AdminLevel.Admin
                };
            }

            var success = await AddAdmin(
                targetUserId,
                userInfo?.Username ?? "",
                userInfo?.FirstName ?? "",
                level,
                userId
            );

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ {displayName}{usernameDisplay} / <code>{targetUserId}</code> назначен администратором с уровнем <b>{level}</b>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось назначить администратора. Возможно:\n" +
                          "• Пользователь уже администратор\n" +
                          "• У вас недостаточно прав",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleRemoveAdmin(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /removeadmin <code>ID</code> или @username",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var success = await RemoveAdmin(targetUserId, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Администратор {displayName}{usernameDisplay} / <code>{targetUserId}</code> удален.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось удалить администратора. Возможно:\n" +
                          "• Пользователь не является администратором\n" +
                          "• У вас недостаточно прав\n" +
                          "• Это владелец бота",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleSetAdminLevel(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 3)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /setadminlevel <code>ID</code> или @username <level>\n\n" +
                      "Уровни:\n" +
                      "• admin - обычный администратор\n" +
                      "• super - суперадминистратор",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var level = parts[2].ToLower() switch
            {
                "super" => AdminLevel.SuperAdmin,
                "admin" => AdminLevel.Admin,
                _ => (AdminLevel?)null
            };

            if (level == null)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверный уровень. Используйте: admin или super",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var success = ChangeAdminLevel(targetUserId, level.Value, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Уровень администратора {displayName}{usernameDisplay} / <code>{targetUserId}</code> изменен на <b>{level}</b>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось изменить уровень. Возможно:\n" +
                          "• Пользователь не является администратором\n" +
                          "• У вас недостаточно прав\n" +
                          "• Это владелец бота",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleAddGold(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 3)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /addgold <code>ID</code> или @username <amount>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            if (!int.TryParse(parts[2], out var amount))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверное количество золота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            if (amount <= 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Количество золота должно быть положительным числом.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var success = await AddGoldToUser(targetUserId, amount, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Пользователю {displayName}{usernameDisplay} / <code>{targetUserId}</code> добавлено <b>{amount}</b> золота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось добавить золото. Возможно, пользователь не существует.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleRemoveGold(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 3)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /removegold <code>ID</code> или @username <amount>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            if (!int.TryParse(parts[2], out var amount))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверное количество золота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            if (amount <= 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Количество золота должно быть положительным числом.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var success = await RemoveGoldFromUser(targetUserId, amount, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ У пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code> удалено <b>{amount}</b> золота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось удалить золото. Возможно:\n" +
                          "• Пользователь не существует\n" +
                          "• У пользователя недостаточно золота",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleSetGold(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 3)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /setgold <code>ID</code> или @username <amount>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            if (!int.TryParse(parts[2], out var amount))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверное количество золота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            if (amount < 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Количество золота не может быть отрицательным.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            if (!_users.ContainsKey(targetUserId))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Пользователь не существует в базе данных.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var user = _users[targetUserId];
            user.Gold = amount;
            _users[targetUserId] = user;
            SaveUsers();

            var displayName = userInfo?.FirstName ?? user.FirstName ?? "Пользователь";
            var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" :
                                  (!string.IsNullOrEmpty(user.Username) ? $" (@{user.Username})" : "");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Установлено <b>{amount}</b> золота для пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code>.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogInfo($"Админ {userId} установил {amount} золота пользователю {targetUserId}");
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleAddCard(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 4)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /addcard <code>ID</code> или @username <rarity> <card_name>\n\n" +
                      "Редкости: common, rare, epic, legendary, champion\n" +
                      "Имя карты: название файла без расширения",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var rarity = parts[2].ToLower();
            var cardName = string.Join(" ", parts.Skip(3));

            if (!_rarities.ContainsKey(rarity))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверная редкость. Используйте: common, rare, epic, legendary, champion, exclusive",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var success = await AddCardToUser(targetUserId, rarity, cardName, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Карта <b>{cardName}</b> ({rarity}) добавлена пользователю {displayName}{usernameDisplay} / <code>{targetUserId}</code>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось добавить карту. Возможно:\n" +
                          "• Карта уже есть у пользователя\n" +
                          "• Карта не найдена\n" +
                          "• Пользователь не существует",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleRemoveCard(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 3)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /removecard <code>ID</code> или @username <card_id>\n\n" +
                      "Card ID: rarity:filename (например: common:card1.jpg)",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var cardId = parts[2];

            var success = await RemoveCardFromUser(targetUserId, cardId, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Карта <code>{cardId}</code> удалена у пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось удалить карту. Возможно:\n" +
                          "• Карты нет у пользователя\n" +
                          "• Пользователь не существует\n" +
                          "• Неверный ID карты",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleResetCooldown(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /resetcd <code>ID</code> или @username",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var success = await ResetUserCooldown(targetUserId, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Кулдаун сброшен для пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось сбросить кулдаун. Возможно, пользователь не существует.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleResetStats(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /resetstats <code>ID</code> или @username",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var success = await ResetUserStats(targetUserId, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Статистика сброшена для пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code>.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось сбросить статистику. Возможно, пользователь не существует.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleDeleteUser(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /deleteuser <code>ID</code> или @username",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            var success = await DeleteUser(targetUserId, userId);

            if (success)
            {
                var displayName = userInfo?.FirstName ?? "Пользователь";
                var usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Пользователь {displayName}{usernameDisplay} / <code>{targetUserId}</code> удален.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Не удалось удалить пользователя. Возможно:\n" +
                          "• Пользователь не существует\n" +
                          "• Это администратор\n" +
                          "• Это владелец бота",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleBroadcast(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /broadcast <сообщение>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var message = string.Join(" ", parts.Skip(1));
        var users = _users.Keys.ToList();
        var sentCount = 0;
        var failedCount = 0;

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"📢 Рассылка начата. Получателей: {users.Count}",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );

        foreach (var user in users)
        {
            try
            {
                await bot.SendMessage(
                    chatId: user,
                    messageThreadId: messageThreadId,
                    text: $"📢 <b>Объявление от администрации:</b>\n\n{message}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                sentCount++;
                await Task.Delay(100); // Задержка чтобы не превысить лимиты API
            }
            catch
            {
                failedCount++;
            }
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"✅ Рассылка завершена.\n\n" +
                  $"📨 Отправлено: {sentCount}\n" +
                  $"❌ Не отправлено: {failedCount}",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );

        LogInfo($"Админ {userId} сделал рассылку: {sentCount} отправлено, {failedCount} ошибок");
    }

    private static async Task HandleStatsAll(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var totalUsers = _users.Count;
        var totalCards = _users.Values.Sum(u => u.TotalCards);
        var totalGold = _users.Values.Sum(u => u.Gold);
        var activeToday = _users.Values.Count(u => u.LastCardTime.Date == DateTime.UtcNow.Date);
        var newToday = _users.Values.Count(u => u.Registered.Date == DateTime.UtcNow.Date);

        var message = $"📊 <b>Общая статистика бота:</b>\n\n" +
                     $"👥 Всего пользователей: <b>{totalUsers}</b>\n" +
                     $"🎴 Всего открыто карт: <b>{totalCards}</b>\n" +
                     $"💰 Всего золота в системе: <b>{totalGold}</b>\n" +
                     $"📈 Активных сегодня: <b>{activeToday}</b>\n" +
                     $"🆕 Новых сегодня: <b>{newToday}</b>\n\n" +
                     $"<i>Статистика на {DateTime.UtcNow:dd.MM.yyyy HH:mm}</i>";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleFindUser(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /finduser <username или часть имени или ID>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var searchTerm = parts[1].ToLower().Trim();

        // Если это ID
        if (long.TryParse(searchTerm, out long searchId))
        {
            if (_users.ContainsKey(searchId))
            {
                var user = _users[searchId];
                var message = $"🔍 <b>Результаты поиска по ID {searchId}:</b>\n\n" +
                             $"👤 Имя: {user.FirstName} {user.LastName}\n" +
                             $"📱 Username: {(string.IsNullOrEmpty(user.Username) ? "не установлен" : "@" + user.Username)}\n" +
                             $"💰 Золото: {user.Gold}\n" +
                             $"🎴 Карт: {user.TotalCards}";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: message,
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }
        }

        // Ищем по username (точное совпадение)
        var exactMatches = _users.Values
            .Where(u => !string.IsNullOrEmpty(u.Username) &&
                        u.Username.Equals(searchTerm, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            var user = exactMatches[0];
            var message = $"🔍 <b>Результаты поиска по @{searchTerm}:</b>\n\n" +
                         $"🆔 ID: <code>{user.UserId}</code>\n" +
                         $"👤 Имя: {user.FirstName} {user.LastName}\n" +
                         $"💰 Золото: {user.Gold}\n" +
                         $"🎴 Карт: {user.TotalCards}";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Ищем по частичному совпадению
        var results = _users.Values
            .Where(u =>
                (u.Username?.ToLower().Contains(searchTerm) == true) ||
                u.FirstName?.ToLower().Contains(searchTerm) == true ||
                u.LastName?.ToLower().Contains(searchTerm) == true)
            .Take(10)
            .ToList();

        if (results.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Пользователи по запросу '{searchTerm}' не найдены.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var messageList = $"🔍 <b>Результаты поиска:</b> \"{searchTerm}\"\n\n";

        for (int i = 0; i < results.Count; i++)
        {
            var user = results[i];
            var displayName = !string.IsNullOrEmpty(user.FirstName)
                ? $"{user.FirstName}{(string.IsNullOrEmpty(user.LastName) ? "" : $" {user.LastName}")}"
                : $"Игрок #{user.UserId}";

            messageList += $"{i + 1}. {displayName}";
            if (!string.IsNullOrEmpty(user.Username))
                messageList += $" (@{user.Username})";

            messageList += $"\n   🆔 <code>{user.UserId}</code>";
            messageList += $"\n   💰 <code>{user.Gold}</code> золота | 🎴 <code>{user.TotalCards}</code> карт\n\n";
        }

        if (messageList.Length > MAX_MESSAGE_LENGTH)
        {
            messageList = messageList.Substring(0, MAX_MESSAGE_LENGTH - 100) + "...";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: messageList,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleUserInfo(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /userinfo <code>ID</code> или @username",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            if (!_users.ContainsKey(targetUserId))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"❌ Пользователь {targetUserId} не найден в базе данных.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var user = _users[targetUserId];
            var cardCount = _userCards.ContainsKey(targetUserId) ? _userCards[targetUserId].Count : 0;
            var isAdmin = IsAdmin(targetUserId);
            var adminLevel = GetAdminLevel(targetUserId);

            var message = $"👤 <b>Информация о пользователе:</b>\n\n" +
                         $"🆔 ID: <code>{user.UserId}</code>\n" +
                         $"👤 Имя: {user.FirstName} {user.LastName}\n" +
                         $"📱 Username: {(string.IsNullOrEmpty(user.Username) ? "не установлен" : "@" + user.Username)}\n" +
                         $"💰 Золото: <b>{user.Gold}</b>\n" +
                         $"💎 Гемы: <b>{(_userGems.ContainsKey(targetUserId) ? _userGems[targetUserId] : 0)}</b>\n" +
                         $"🎴 Уникальных карт: <b>{cardCount}</b>\n" +
                         $"📊 Всего открыто: <b>{user.TotalCards}</b>\n\n" +
                         $"📈 <b>Статистика по редкостям:</b>\n" +
                         $"🔵 Обычные: {user.CommonCards}\n" +
                         $"🟠 Редкие: {user.RareCards}\n" +
                         $"🟣 Эпические: {user.EpicCards}\n" +
                         $"⚪ Легендарные: {user.LegendaryCards}\n" +
                         $"🌟 Чемпионские: {user.ChampionCards}\n\n" +
                         $"📅 Регистрация: {user.Registered:dd.MM.yyyy HH:mm}\n" +
                         $"⏰ Последняя карта: {(user.LastCardTime == DateTime.MinValue ? "никогда" : user.LastCardTime.ToString("dd.MM.yyyy HH:mm"))}\n" +
                         $"👮 Админ: {(isAdmin ? $"Да ({adminLevel})" : "Нет")}";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // НОВЫЕ МЕТОДЫ: Управление каналом
    // ============================

    // 1. Отправка текстового сообщения в канал
    private static async Task HandleSendToChannel(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        // Проверяем права администратора
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только администраторы могут отправлять сообщения в канал",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /sendtochannel <текст сообщения>\n\n" +
                      "Пример: /sendtochannel Новое объявление!\n\n" +
                      "Сообщение будет отправлено в канал, указанный в настройках бота.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var messageText = string.Join(" ", parts.Skip(1));

        try
        {
            // Проверяем, что _channelId установлен
            if (_channelId == 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ ID канала не настроен. Проверьте конфигурацию бота.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            LogInfo($"Админ {userId} пытается отправить сообщение в канал {_channelId}: {messageText}");

            // Экранируем HTML-теги или отправляем без парсинга
            try
            {
                // Пробуем отправить с HTML парсингом
                var sentMessage = await bot.SendMessage(
                    chatId: _channelId,
                    text: messageText,
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );

                // Отправляем подтверждение администратору
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Сообщение отправлено в канал!\n\n" +
                          $"📝 Текст: {messageText}\n" +
                          $"🆔 ID сообщения: <code>{sentMessage.MessageId}</code>\n" +
                          $"👤 Отправил: <code>{userId}</code>\n\n" +
                          $"<i>Сообщение будет видно подписчикам канала</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
            {
                // Если не получается с HTML, отправляем без парсинга
                LogInfo($"Ошибка HTML парсинга, отправляем без форматирования: {ex.Message}");

                var sentMessage = await bot.SendMessage(
                    chatId: _channelId,
                    text: messageText,
                    parseMode: ParseMode.None, // Без парсинга
                    cancellationToken: ct
                );

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Сообщение отправлено в канал (без форматирования)!\n\n" +
                          $"📝 Текст: {messageText}\n" +
                          $"🆔 ID сообщения: <code>{sentMessage.MessageId}</code>\n" +
                          $"👤 Отправил: <code>{userId}</code>\n\n" +
                          $"<i>Сообщение отправлено без HTML форматирования из-за некорректных тегов</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }

            LogInfo($"Админ {userId} успешно отправил сообщение в канал");
        }
        catch (ApiRequestException ex)
        {
            string errorDetails = "";

            if (ex.ErrorCode == 400)
            {
                errorDetails = "Канал не найден или у бота нет прав.\n\n" +
                              "Что проверить:\n" +
                              "1. Бот добавлен в канал как администратор?\n" +
                              "2. Бот имеет право отправлять сообщения?\n" +
                              "3. Правильный ли ID канала?\n\n" +
                              $"Текущий ID канала: <code>{_channelId}</code>";
            }
            else if (ex.ErrorCode == 403)
            {
                errorDetails = "У бота нет прав на отправку сообщений в этот канал.";
            }
            else if (ex.ErrorCode == 429)
            {
                errorDetails = "Слишком много запросов. Подождите немного.";
            }
            else
            {
                errorDetails = $"Код ошибки: {ex.ErrorCode}\nСообщение: {ex.Message}";
            }

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка отправки в канал:\n\n{errorDetails}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogError($"Ошибка отправки в канал от {userId}: {ex.ErrorCode} - {ex.Message}");
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Неизвестная ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogError($"Неизвестная ошибка при отправке в канал: {ex.Message}");
        }
    }

    // 2. Отправка случайной карты в канал
    private static async Task HandleSendPhotoToChannel(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        string rarity;

        if (parts.Length < 2)
        {
            // Если редкость не указана - выбираем случайную
            rarity = GetRandomRarity();
        }
        else
        {
            rarity = parts[1].ToLower();
            if (!_rarities.ContainsKey(rarity))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Неверная редкость. Используйте: common, rare, epic, legendary, champion\n" +
                          "Или без параметра для случайной редкости.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }
        }

        if (!_imageFilesCache.ContainsKey(rarity) || _imageFilesCache[rarity].Length == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Нет карт с редкостью {rarity}.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var files = _imageFilesCache[rarity];
        var randomFile = files[_random.Next(files.Length)];
        var fileName = Path.GetFileName(randomFile);
        var cardName = Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ');

        var reward = _rarities[rarity];
        var rarityDisplayName = rarity switch
        {
            "common" => "Обычная",
            "rare" => "Редкая",
            "epic" => "Эпическая",
            "legendary" => "Легендарная",
            "champion" => "Чемпионская",
            _ => "Неизвестная"
        };

        var caption = $"<b>{reward.emoji} {rarityDisplayName}</b>\n" +
                      $"🎴 <b>{cardName}</b>\n" +
                      $"💰 Стоимость: {reward.gold} золота";

        try
        {
            using var stream = File.OpenRead(randomFile);
            var sentMessage = await bot.SendPhoto(
                chatId: _channelId,
                photo: new InputFileStream(stream, fileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Карта отправлена в канал {_channelUsername}\n" +
                      $"🎴 Редкость: {rarityDisplayName}\n" +
                      $"📝 Название: {cardName}\n" +
                      $"🆔 ID сообщения: <code>{sentMessage.MessageId}</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogInfo($"Админ {userId} отправил карту {rarity} в канал: {cardName}");
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка отправки фото: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // 3. Закрепление сообщения в канале
    private static async Task HandlePinMessage(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /pinmessage <ID_сообщения>\n\n" +
                      "ID сообщения можно получить:\n" +
                      "1. Из ссылки на сообщение\n" +
                      "2. Из ответа бота при отправке\n" +
                      "Пример: /pinmessage 123",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (!int.TryParse(parts[1], out var messageId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверный ID сообщения. Должно быть число.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            await bot.PinChatMessage(
                chatId: _channelId,
                messageId: messageId,
                cancellationToken: ct
            );

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Сообщение <code>{messageId}</code> закреплено в канале {_channelUsername}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogInfo($"Админ {userId} закрепил сообщение {messageId} в канале");
        }
        catch (ApiRequestException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка закрепления: {ex.Message}\n\n" +
                      "Проверьте:\n" +
                      "1. Бот имеет право закреплять сообщения\n" +
                      "2. ID сообщения правильное\n" +
                      "3. Сообщение существует в канале",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // 4. Открепление сообщения в канале
    private static async Task HandleUnpinMessage(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            await bot.UnpinChatMessage(
                chatId: _channelId,
                messageId: 0, // 0 = открепить все сообщения
                cancellationToken: ct
            );

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Все сообщения откреплены в канале {_channelUsername}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );

            LogInfo($"Админ {userId} открепил сообщения в канале");
        }
        catch (ApiRequestException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка открепления: {ex.Message}\n\n" +
                      "Проверьте права бота в канале.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // АДМИН ФУНКЦИИ: Работа с пользователями
    // ============================

    private static async Task<bool> AddGoldToUser(long userId, int amount, long adminId)
    {
        if (!IsAdmin(adminId) || amount <= 0)
            return false;

        if (!_users.ContainsKey(userId))
            return false;

        var user = _users[userId];
        user.Gold += amount;
        _users[userId] = user;
        SaveUsers();

        // Логируем действие
        var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
        LogInfo($"{adminName} выдал {amount} золота пользователю {userId}");

        return true;
    }

    private static async Task<bool> RemoveGoldFromUser(long userId, int amount, long adminId)
    {
        if (!IsAdmin(adminId) || amount <= 0)
            return false;

        if (!_users.ContainsKey(userId))
            return false;

        var user = _users[userId];
        if (user.Gold < amount)
            return false;

        user.Gold -= amount;
        _users[userId] = user;
        SaveUsers();

        // Логируем действие
        var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
        LogInfo($"{adminName} забрал {amount} золота у пользователя {userId}");

        return true;
    }

    private static async Task<bool> AddCardToUser(long userId, string rarity, string cardName, long adminId)
    {
        if (!IsAdmin(adminId))
            return false;

        if (!_imageFilesCache.ContainsKey(rarity) || _imageFilesCache[rarity].Length == 0)
            return false;

        // Ищем карту по имени
        var files = _imageFilesCache[rarity];
        var cardFile = files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f)
                .Replace('_', ' ')
                .Replace('-', ' ')
                .Equals(cardName, StringComparison.OrdinalIgnoreCase));

        if (cardFile == null)
            return false;

        var fileName = Path.GetFileName(cardFile);
        var cardId = $"{rarity}:{fileName}";

        if (!_userCards.ContainsKey(userId))
        {
            _userCards[userId] = new HashSet<string>();
        }

        // Добавляем карту
        var added = _userCards[userId].Add(cardId);
        if (added)
        {
            // Обновляем статистику пользователя
            if (_users.ContainsKey(userId))
            {
                var user = _users[userId];
                user.TotalCards++;
                switch (rarity)
                {
                    case "common": user.CommonCards++; break;
                    case "rare": user.RareCards++; break;
                    case "epic": user.EpicCards++; break;
                    case "legendary": user.LegendaryCards++; break;
                    case "champion": user.ChampionCards++; break;
                }
                _users[userId] = user;
                SaveUsers();
            }

            SaveUserCards();

            // Логируем действие
            var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
            LogInfo($"{adminName} добавил карту {cardName} ({rarity}) пользователю {userId}");
        }

        return added;
    }

    private static async Task<bool> RemoveCardFromUser(long userId, string cardId, long adminId)
    {
        if (!IsAdmin(adminId))
            return false;

        if (!_userCards.ContainsKey(userId) || !_userCards[userId].Contains(cardId))
            return false;

        var removed = _userCards[userId].Remove(cardId);
        if (removed)
        {
            SaveUserCards();

            // Логируем действие
            var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
            LogInfo($"{adminName} удалил карту {cardId} у пользователя {userId}");
        }

        return removed;
    }

    private static async Task<bool> ResetUserCooldown(long userId, long adminId)
    {
        if (!IsAdmin(adminId))
            return false;

        if (!_users.ContainsKey(userId))
            return false;

        var user = _users[userId];
        user.LastCardTime = DateTime.MinValue;
        _users[userId] = user;
        SaveUsers();

        // Логируем действие
        var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
        LogInfo($"{adminName} сбросил кулдаун пользователю {userId}");

        return true;
    }

    private static async Task<bool> ResetUserStats(long userId, long adminId)
    {
        if (!IsAdmin(adminId))
            return false;

        if (!_users.ContainsKey(userId))
            return false;

        var user = _users[userId];
        user.Gold = 0;
        user.TotalCards = 0;
        user.CommonCards = 0;
        user.RareCards = 0;
        user.EpicCards = 0;
        user.LegendaryCards = 0;
        user.ChampionCards = 0;
        user.LastCardTime = DateTime.MinValue;
        _users[userId] = user;
        SaveUsers();

        // Очищаем карты пользователя
        if (_userCards.ContainsKey(userId))
        {
            _userCards[userId].Clear();
            SaveUserCards();
        }

        // Очищаем колоду пользователя
        if (_userDecks.ContainsKey(userId))
        {
            _userDecks[userId].CardIds.Clear();
            _userDecks[userId].FavoriteCardId = "";
            SaveUserDecks();
        }

        // Логируем действие
        var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
        LogInfo($"{adminName} сбросил статистику пользователя {userId}");

        return true;
    }

    private static async Task<bool> DeleteUser(long userId, long adminId)
    {
        if (!IsAdmin(adminId))
            return false;

        if (IsAdmin(userId) && !IsOwner(adminId))
            return false; // Админ не может удалить другого админа

        if (IsOwner(userId))
            return false; // Нельзя удалить владельца

        var userRemoved = _users.TryRemove(userId, out _);
        if (userRemoved) SaveUsers();

        var cardsRemoved = _userCards.TryRemove(userId, out _);
        if (cardsRemoved) SaveUserCards();

        var deckRemoved = _userDecks.TryRemove(userId, out _);
        if (deckRemoved) SaveUserDecks();

        // Логируем действие
        if (userRemoved || cardsRemoved || deckRemoved)
        {
            var adminName = IsOwner(adminId) ? "Владелец" : $"Админ {adminId}";
            LogInfo($"{adminName} удалил пользователя {userId}");
        }

        return userRemoved || cardsRemoved || deckRemoved;
    }

    // ============================
    // УТИЛИТЫ: Поиск пользователей
    // ============================

    private static async Task<(long userId, User userInfo)> ResolveUserWithInfo(ITelegramBotClient bot, string input, CancellationToken ct)
    {
        // Очищаем входные данные
        input = input.Trim();

        // Убираем лишние символы
        if (input.StartsWith("@"))
        {
            input = input.Substring(1);
        }

        LogInfo($"ResolveUserWithInfo: ищем пользователя '{input}'");

        // Сначала пробуем найти по ID
        if (long.TryParse(input, out long parsedId))
        {
            LogInfo($"ResolveUserWithInfo: введен ID {parsedId}");

            if (_users.ContainsKey(parsedId))
            {
                var userData = _users[parsedId];
                var userInfo = new User
                {
                    Id = parsedId,
                    FirstName = userData.FirstName,
                    LastName = userData.LastName,
                    Username = userData.Username
                };
                LogInfo($"ResolveUserWithInfo: найден пользователь в базе по ID: {userData.FirstName} (@{userData.Username})");
                return (parsedId, userInfo);
            }
            else
            {
                LogInfo($"ResolveUserWithInfo: пользователь с ID {parsedId} не найден в базе");
                return (parsedId, new User { Id = parsedId, FirstName = "Пользователь", Username = input });
            }
        }

        // Нормализуем входную строку для поиска (убираем все специальные символы)
        string normalizedInput = NormalizeUsername(input);
        LogInfo($"ResolveUserWithInfo: нормализованный ввод '{normalizedInput}'");

        // Сначала ищем по точному совпадению username (без учета регистра)
        var exactMatches = _users.Values
            .Where(u => !string.IsNullOrEmpty(u.Username))
            .Where(u => u.Username.Equals(input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            var match = exactMatches[0];
            LogInfo($"ResolveUserWithInfo: найден пользователь по точному совпадению username: {match.FirstName} (@{match.Username})");
            var userInfo = new User
            {
                Id = match.UserId,
                FirstName = match.FirstName,
                LastName = match.LastName,
                Username = match.Username
            };
            return (match.UserId, userInfo);
        }

        // Если не нашли по точному совпадению, ищем по нормализованному имени
        var normalizedMatches = _users.Values
            .Where(u => !string.IsNullOrEmpty(u.Username))
            .Where(u => NormalizeUsername(u.Username).Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (normalizedMatches.Count == 1)
        {
            var match = normalizedMatches[0];
            LogInfo($"ResolveUserWithInfo: найден пользователь по нормализованному username: {match.FirstName} (@{match.Username})");
            var userInfo = new User
            {
                Id = match.UserId,
                FirstName = match.FirstName,
                LastName = match.LastName,
                Username = match.Username
            };
            return (match.UserId, userInfo);
        }

        // Если все еще не нашли, ищем по частичному совпадению
        var partialMatches = _users.Values
            .Where(u => !string.IsNullOrEmpty(u.Username))
            .Where(u => u.Username.Contains(input, StringComparison.OrdinalIgnoreCase) ||
                       NormalizeUsername(u.Username).Contains(normalizedInput, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (partialMatches.Count == 1)
        {
            var match = partialMatches[0];
            LogInfo($"ResolveUserWithInfo: найден пользователь по частичному совпадению: {match.FirstName} (@{match.Username})");
            var userInfo = new User
            {
                Id = match.UserId,
                FirstName = match.FirstName,
                LastName = match.LastName,
                Username = match.Username
            };
            return (match.UserId, userInfo);
        }
        else if (partialMatches.Count > 1)
        {
            // Нашли несколько пользователей - показываем список
            var usernames = string.Join(", ", partialMatches.Select(u => $"@{u.Username}"));
            throw new ArgumentException($"Найдено несколько пользователей по запросу '{input}': {usernames}. Пожалуйста, уточните запрос или используйте ID.");
        }

        // Если ничего не нашли, пробуем поискать по имени или фамилии
        var nameMatches = _users.Values
            .Where(u =>
                (!string.IsNullOrEmpty(u.FirstName) && u.FirstName.Contains(input, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(u.LastName) && u.LastName.Contains(input, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (nameMatches.Count == 1)
        {
            var match = nameMatches[0];
            LogInfo($"ResolveUserWithInfo: найден пользователь по имени/фамилии: {match.FirstName} {match.LastName} (@{match.Username})");
            var userInfo = new User
            {
                Id = match.UserId,
                FirstName = match.FirstName,
                LastName = match.LastName,
                Username = match.Username
            };
            return (match.UserId, userInfo);
        }
        else if (nameMatches.Count > 1)
        {
            var names = string.Join(", ", nameMatches.Select(u => $"{u.FirstName} {u.LastName}"));
            throw new ArgumentException($"Найдено несколько пользователей по имени '{input}': {names}. Используйте username или ID.");
        }

        // Если ничего не нашли, возвращаем исключение
        throw new ArgumentException($"Пользователь '{input}' не найден в базе данных. Убедитесь, что пользователь существует и использует бота.");
    }

    // Вспомогательный метод для нормализации username (убираем специальные символы)
    private static string NormalizeUsername(string username)
    {
        if (string.IsNullOrEmpty(username))
            return username;

        // Убираем все символы, кроме букв и цифр
        return new string(username
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray());
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Топ игроков (/top)
    // ============================

    private static async Task SendLeaderboardByMoney(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct, int? editMessageId = null)
    {
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            if (editMessageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: editMessageId.Value,
                    text: MessageManager.Get("access_denied", missingText),
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("📺 Канал", _channelLink)
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Проверить", "check_membership")
                    }
                    }),
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: MessageManager.Get("access_denied", missingText),
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("📺 Канал", _channelLink)
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Проверить", "check_membership")
                    }
                    }),
                    cancellationToken: ct
                );
            }
            return;
        }
        // Сортируем по золоту
        var topUsers = _users.Values
            .Where(u => u.Gold > 0)
            .OrderByDescending(u => u.Gold)
            .ThenByDescending(u => _userCards.ContainsKey(u.UserId) ? _userCards[u.UserId].Count : 0)
            .Take(12)
            .ToList();

        if (topUsers.Count == 0)
        {
            var emptyKeyboard = new InlineKeyboardMarkup(new[]
            {
            new[] { InlineKeyboardButton.WithCallbackData("🎴 Топ по картам", "top_card") }
        });

            if (editMessageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: editMessageId.Value,
                    text: "🏆 <b>ТОП ПО ЗОЛОТУ</b>\n\nПока никого нет. Будьте первым!",
                    parseMode: ParseMode.Html,
                    replyMarkup: emptyKeyboard,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "🏆 <b>ТОП ПО ЗОЛОТУ</b>\n\nПока никого нет. Будьте первым!",
                    parseMode: ParseMode.Html,
                    replyMarkup: emptyKeyboard,
                    cancellationToken: ct
                );
            }
            return;
        }

        // Формируем минималистичное сообщение с именами БЕЗ @
        var message = "<b>🏆 ТОП ПО ЗОЛОТУ</b>\n\n";

        for (int i = 0; i < topUsers.Count; i++)
        {
            var user = topUsers[i];
            var medal = i switch
            {
                0 => "🥇",
                1 => "🥈",
                2 => "🥉",
                _ => $"<b>{i + 1}.</b>"
            };

            // Получаем имя БЕЗ @
            var displayName = GetDisplayNameWithoutAt(user);
            message += $"{medal} {displayName} — <code>{user.Gold}</code>💰\n";
        }

        // Создаем компактную клавиатуру с Telegram ссылками
        var keyboard = CreateCompactTopKeyboard(topUsers, userId, "money");

        if (editMessageId.HasValue)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: editMessageId.Value,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
    }

    private static string GetDisplayNameWithoutAt(UserData user)
    {
        if (!string.IsNullOrEmpty(user.Username) && user.Username != "null")
        {
            return user.Username; // Без @
        }

        if (!string.IsNullOrEmpty(user.FirstName))
        {
            string fullName = user.FirstName;
            if (!string.IsNullOrEmpty(user.LastName))
            {
                fullName += " " + user.LastName;
            }
            return fullName;
        }

        return $"Игрок {user.UserId}";
    }


    // ============================
    // ВСПОМОГАТЕЛЬНЫЙ МЕТОД ДЛЯ СОЗДАНИЯ ПРОСТОЙ КЛАВИАТУРЫ
    // ============================

    

    private static async Task SendLeaderboardByCards(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct, int? editMessageId = null)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            if (editMessageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: editMessageId.Value,
                    text: MessageManager.Get("access_denied", missingText),
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("📺 Канал", _channelLink)
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Проверить", "check_membership")
                    }
                    }),
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: MessageManager.Get("access_denied", missingText),
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                    new[]
                    {
                        InlineKeyboardButton.WithUrl("📺 Канал", _channelLink)
                    },
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Проверить", "check_membership")
                    }
                    }),
                    cancellationToken: ct
                );
            }
            return;
        }

    // Сортируем по количеству уникальных карт
    var topUsers = _users.Values
            .Where(u => _userCards.ContainsKey(u.UserId) && _userCards[u.UserId].Count > 0)
            .Select(u => new
            {
                User = u,
                UniqueCards = _userCards.ContainsKey(u.UserId) ? _userCards[u.UserId].Count : 0
            })
            .OrderByDescending(x => x.UniqueCards)
            .ThenByDescending(x => x.User.Gold)
            .Take(12)
            .ToList();

        if (topUsers.Count == 0)
        {
            var emptyKeyboard = new InlineKeyboardMarkup(new[]
            {
            new[] { InlineKeyboardButton.WithCallbackData("💰 Топ по золоту", "top_money") }
        });

            if (editMessageId.HasValue)
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: editMessageId.Value,
                    text: "🎴 <b>ТОП ПО КАРТАМ</b>\n\nПока никого нет. Будьте первым!",
                    parseMode: ParseMode.Html,
                    replyMarkup: emptyKeyboard,
                    cancellationToken: ct
                );
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "🎴 <b>ТОП ПО КАРТАМ</b>\n\nПока никого нет. Будьте первым!",
                    parseMode: ParseMode.Html,
                    replyMarkup: emptyKeyboard,
                    cancellationToken: ct
                );
            }
            return;
        }

        // Формируем минималистичное сообщение с именами БЕЗ @
        var message = "<b>🎴 ТОП ПО КАРТАМ</b>\n\n";

        for (int i = 0; i < topUsers.Count; i++)
        {
            var item = topUsers[i];
            var user = item.User;
            var medal = i switch
            {
                0 => "🥇",
                1 => "🥈",
                2 => "🥉",
                _ => $"<b>{i + 1}.</b>"
            };

            // Получаем имя БЕЗ @
            var displayName = GetDisplayNameWithoutAt(user);
            message += $"{medal} {displayName} — <code>{item.UniqueCards}</code>🎴\n";
        }

        // Создаем компактную клавиатуру с Telegram ссылками
        var topUsersData = topUsers.Select(x => x.User).ToList();
        var keyboard = CreateCompactTopKeyboard(topUsersData, userId, "cards");

        if (editMessageId.HasValue)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: editMessageId.Value,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
    }

    private static InlineKeyboardMarkup CreateCompactTopKeyboard(List<UserData> topUsers, long currentUserId, string topType)
    {
        var keyboardButtons = new List<List<InlineKeyboardButton>>();

        // 1-е место - на всю ширину
        if (topUsers.Count >= 1)
        {
            var user = topUsers[0];
            var buttonText = GetCompactButtonText(user, 1);

            // Создаем URL кнопку в Telegram профиль
            string url = GetTelegramProfileUrl(user);
            keyboardButtons.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithUrl(buttonText, url)
        });
        }

        // 2-е и 3-е места - по половине
        if (topUsers.Count >= 2)
        {
            var row2 = new List<InlineKeyboardButton>();

            // 2-е место
            var user2 = topUsers[1];
            row2.Add(InlineKeyboardButton.WithUrl(
                GetCompactButtonText(user2, 2),
                GetTelegramProfileUrl(user2)));

            // 3-е место
            if (topUsers.Count >= 3)
            {
                var user3 = topUsers[2];
                row2.Add(InlineKeyboardButton.WithUrl(
                    GetCompactButtonText(user3, 3),
                    GetTelegramProfileUrl(user3)));
            }

            keyboardButtons.Add(row2);
        }

        // Остальные места - по три в ряд
        if (topUsers.Count >= 4)
        {
            var remainingUsers = topUsers.Skip(3).ToList();
            for (int i = 0; i < remainingUsers.Count; i += 3)
            {
                var row = new List<InlineKeyboardButton>();

                for (int j = 0; j < 3 && i + j < remainingUsers.Count; j++)
                {
                    var user = remainingUsers[i + j];
                    var position = i + j + 4; // +4 потому что пропустили первые 3 места
                    row.Add(InlineKeyboardButton.WithUrl(
                        GetCompactButtonText(user, position),
                        GetTelegramProfileUrl(user)));
                }

                keyboardButtons.Add(row);
            }
        }

        // Нижняя кнопка - переход на другой топ
        string otherTopText = topType == "money" ? "🎴 Топ по картам" : "💰 Топ по золоту";
        string otherTopCallback = topType == "money" ? "top_card" : "top_money";

        keyboardButtons.Add(new List<InlineKeyboardButton>
    {
        InlineKeyboardButton.WithCallbackData(otherTopText, otherTopCallback)
    });

        // Кнопка обновления
        keyboardButtons.Add(new List<InlineKeyboardButton>
    {
        InlineKeyboardButton.WithCallbackData("🔄 Обновить", $"refresh_{topType}")
    });

        return new InlineKeyboardMarkup(keyboardButtons);
    }

    private static string GetTelegramProfileUrl(UserData user)
    {
        if (!string.IsNullOrEmpty(user.Username) && user.Username != "null")
        {
            return $"https://t.me/{user.Username}";
        }
        else
        {
            return $"tg://user?id={user.UserId}";
        }
    }

    private static string GetCompactButtonText(UserData user, int position)
    {
        string medal = position switch
        {
            1 => "🥇",
            2 => "🥈",
            3 => "🥉",
            _ => $"{position}."
        };

        // Получаем короткое имя БЕЗ @
        string shortName;
        if (!string.IsNullOrEmpty(user.Username) && user.Username != "null")
        {
            // Используем username без @
            shortName = user.Username;
            if (shortName.Length > 10)
                shortName = shortName.Substring(0, 9) + "…";
        }
        else if (!string.IsNullOrEmpty(user.FirstName))
        {
            shortName = user.FirstName;
            if (shortName.Length > 8)
                shortName = shortName.Substring(0, 7) + "…";
        }
        else
        {
            shortName = $"#{user.UserId.ToString().Substring(Math.Max(0, user.UserId.ToString().Length - 4))}";
        }

        return $"{medal} {shortName}";
    }

    private static string GetDisplayNameWithTelegramLink(UserData user)
    {
        if (!string.IsNullOrEmpty(user.Username) && user.Username != "null")
        {
            // Показываем username без @, но ссылка работает с @
            return $"<a href=\"https://t.me/{user.Username}\">{user.Username}</a>";
        }

        if (!string.IsNullOrEmpty(user.FirstName))
        {
            // Если есть имя, но нет username, делаем ссылку на профиль через ID
            string fullName = user.FirstName;
            if (!string.IsNullOrEmpty(user.LastName))
            {
                fullName += " " + user.LastName;
            }
            return $"<a href=\"tg://user?id={user.UserId}\">{fullName}</a>";
        }

        // Если ничего нет, показываем ID с ссылкой
        return $"<a href=\"tg://user?id={user.UserId}\">ID: {user.UserId}</a>";
    }
    private static async Task ShowTopMenu(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct, int? editMessageId = null)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

    var message = "🏆 <b>ВЫБЕРИТЕ ТИП ТОПА</b>\n\n"
                    + "💰 <b>Топ по золоту</b> — кто заработал больше всего\n"
                    + "🎴 <b>Топ по картам</b> — у кого самая большая коллекция";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("💰 Топ по золоту", "top_money"),
            InlineKeyboardButton.WithCallbackData("🎴 Топ по картам", "top_card")
        }
    });

        if (editMessageId.HasValue)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: editMessageId.Value,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // ВСПОМОГАТЕЛЬНЫЙ МЕТОД ДЛЯ ПОЛУЧЕНИЯ ИМЕНИ ПОЛЬЗОВАТЕЛЯ
    // ============================

    private static string GetUserDisplayName(UserData user)
    {
        if (string.IsNullOrEmpty(user.FirstName) && string.IsNullOrEmpty(user.LastName))
        {
            if (!string.IsNullOrEmpty(user.Username) && user.Username != "null")
            {
                return $"@{user.Username}";
            }
            return $"Игрок #{user.UserId.ToString().Substring(Math.Max(0, user.UserId.ToString().Length - 4))}";
        }

        // Если есть имя, но оно слишком длинное
        var firstName = user.FirstName ?? "";
        var lastName = user.LastName ?? "";

        // Объединяем имя и фамилию
        var fullName = $"{firstName} {lastName}".Trim();

        // Если полное имя слишком длинное, обрезаем
        if (fullName.Length > 20)
        {
            fullName = fullName.Substring(0, 17) + "...";
        }

        // Если есть username, добавляем сокращенно
        if (!string.IsNullOrEmpty(user.Username) && user.Username != "null" && fullName.Length < 15)
        {
            return $"{fullName} (@{user.Username})";
        }

        return fullName;
    }



    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Золото пользователя (/gold, /balance)
    // ============================

    private static async Task SendUserGold(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

    var user = _users[userId];
        var cardCount = _userCards.ContainsKey(userId) ? _userCards[userId].Count : 0;
        var totalGoldEarned = user.Gold + CalculateTotalGoldSpent(userId);

        string cooldownInfo = "";
        if (user.LastCardTime != DateTime.MinValue)
        {
            var timeSinceLastCard = DateTime.UtcNow - user.LastCardTime;
            var remainingCooldown = COOLDOWN_SECONDS - timeSinceLastCard.TotalSeconds;

            if (remainingCooldown > 0)
            {
                var remainingMinutes = Math.Ceiling(remainingCooldown / 60);
                cooldownInfo = MessageManager.Get("gold_cooldown_active", remainingMinutes);
            }
            else
            {
                cooldownInfo = MessageManager.Get("gold_cooldown_ready");
            }
        }

        var message = $"<b>💰 БАЛАНС</b>\n\n"
                    + $"<b>Текущее золото:</b> <code>{user.Gold}</code>\n"
                    + $"<b>Всего заработано:</b> <code>{totalGoldEarned}</code>\n"
                    + $"<b>Уникальных карт:</b> <code>{cardCount}</code>\n"
                    + $"<b>Всего открыто:</b> <code>{user.TotalCards}</code>\n\n"
                    + $"{cooldownInfo}";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Статистика (/stats)
    // ============================

    private static async Task SendUserStats(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

    var user = _users[userId];
        var total = user.TotalCards;
        var cardCount = _userCards.ContainsKey(userId) ? _userCards[userId].Count : 0;

        if (total == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт! Используйте /pic чтобы открыть первую карту.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var totalPossibleCards = 0;
        foreach (var rarity in _imageFilesCache.Keys)
        {
            totalPossibleCards += _imageFilesCache[rarity].Length;
        }
        var collectionPercentage = totalPossibleCards > 0 ? Math.Round((double)cardCount / totalPossibleCards * 100, 1) : 0;

        var message = $"📊 <b>СТАТИСТИКА ИГРОКА</b>\n\n"
                    + $"<b>💰 Золото:</b> <code>{user.Gold}</code>\n"
                    + $"<b>🎴 Уникальных карт:</b> {cardCount} из {totalPossibleCards} ({collectionPercentage}%)\n"
                    + $"<b>📈 Всего открыто:</b> {total}\n\n"

                    + $"<b>📊 Распределение по редкостям:</b>\n"
                    + $"🔵 Обычные: {user.CommonCards} ({GetPercentage(user.CommonCards, total)}%)\n"
                    + $"🟠 Редкие: {user.RareCards} ({GetPercentage(user.RareCards, total)}%)\n"
                    + $"🟣 Эпические: {user.EpicCards} ({GetPercentage(user.EpicCards, total)}%)\n"
                    + $"⚪ Легендарные: {user.LegendaryCards} ({GetPercentage(user.LegendaryCards, total)}%)\n"
                    + $"🌟 Чемпионские: {user.ChampionCards} ({GetPercentage(user.ChampionCards, total)}%)\n\n"

                    + $"<b>⏰ Последняя карта:</b> {user.LastCardTime.ToString("dd.MM.yyyy HH:mm")}\n"
                    + $"<b>🕐 Кулдаун:</b> {COOLDOWN_SECONDS / 60} минут";

        if (message.Length > MAX_MESSAGE_LENGTH)
        {
            message = message.Substring(0, MAX_MESSAGE_LENGTH - 100) + "...";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Коллекция карт (/mycards)
    // ============================

    private static async Task SendUserCards(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }        

        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции! Используйте /card чтобы открыть первую карту.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var userCards = _userCards[userId].ToList();
        var user = _users[userId];

        var cardsByRarity = new Dictionary<string, int>
    {
        { "common", 0 },
        { "rare", 0 },
        { "epic", 0 },
        { "legendary", 0 },
        { "champion", 0 }
    };

        foreach (var cardId in userCards)
        {
            var parts = cardId.Split(':');
            if (parts.Length >= 1 && cardsByRarity.ContainsKey(parts[0]))
            {
                cardsByRarity[parts[0]]++;
            }
        }

        var totalPossibleCards = 0;
        foreach (var rarity in _imageFilesCache.Keys)
        {
            totalPossibleCards += _imageFilesCache[rarity].Length;
        }
        var collectionPercentage = totalPossibleCards > 0 ? Math.Round((double)userCards.Count / totalPossibleCards * 100, 1) : 0;

        var message = $"<b>🎴 МОЯ КОЛЛЕКЦИЯ КАРТ</b>\n\n"
                    + $"<b>💰 Золото:</b> <code>{user.Gold}</code>\n"
                    + $"<b>📊 Уникальных карт:</b> {userCards.Count} из {totalPossibleCards} ({collectionPercentage}%)\n"
                    + $"<b>📈 Всего открыто:</b> {user.TotalCards}\n\n"

                    + $"<b>📊 Распределение по редкостям:</b>\n"
                    + $"🔵 Обычные: {cardsByRarity["common"]}\n"
                    + $"🟠 Редкие: {cardsByRarity["rare"]}\n"
                    + $"🟣 Эпические: {cardsByRarity["epic"]}\n"
                    + $"⚪ Легендарные: {cardsByRarity["legendary"]}\n"
                    + $"🌟 Чемпионские: {cardsByRarity["champion"]}\n\n";

        if (message.Length > MAX_MESSAGE_LENGTH)
        {
            message = message.Substring(0, MAX_MESSAGE_LENGTH - 100) + "...\n\n<i>Сообщение сокращено</i>";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Шансы выпадения (/chance, /prob)
    // ============================

    private static async Task SendChanceInfo(ITelegramBotClient bot, long chatId, int? messageThreadId, CancellationToken ct)
    {
        var message = MessageManager.Get("chance", COOLDOWN_SECONDS / 60);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId, // ДОБАВЛЕН
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }
    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Кулдаун (/cooldown, /cd) - ОБНОВЛЕННЫЙ
    // ============================

    private static async Task SendCooldownInfo(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

    var user = _users[userId];

        if (user.LastCardTime == DateTime.MinValue)
        {
            var message = MessageManager.Get("cooldown_empty", COOLDOWN_SECONDS / 60);

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var timeSinceLastCard = DateTime.UtcNow - user.LastCardTime;
        var remainingCooldown = COOLDOWN_SECONDS - timeSinceLastCard.TotalSeconds;

        string messageText;
        if (remainingCooldown > 0)
        {
            var remainingMinutes = Math.Ceiling(remainingCooldown / 60);
            var remainingSeconds = Math.Ceiling(remainingCooldown % 60);

            // Креативные сообщения о кулдауне
            var cooldownMessages = MessageManager.GetArray("cooldown_messages");
            var randomMessage = string.Format(
                cooldownMessages[_random.Next(cooldownMessages.Length)],
                remainingMinutes,
                remainingSeconds
            );

            messageText = $"{randomMessage}\n\n" +
                         $"⏰ <b>Последняя карта:</b> {user.LastCardTime:HH:mm:ss}\n" +
                         $"📊 <b>Прошло времени:</b> {Math.Floor(timeSinceLastCard.TotalMinutes)} мин\n" +
                         $"🎯 <b>Полный кулдаун:</b> {COOLDOWN_SECONDS / 60} мин";
        }
        else
        {
            var readyMessages = MessageManager.GetArray("cooldown_ready_messages");
            var randomMessage = readyMessages[_random.Next(readyMessages.Length)];

            messageText = $"{randomMessage}\n\n" +
                         $"⏰ <b>Последняя карта:</b> {user.LastCardTime:HH:mm:ss}\n" +
                         $"📊 <b>Прошло времени:</b> {Math.Floor(timeSinceLastCard.TotalMinutes)} мин\n" +
                         $"🎯 <b>Полный кулдаун:</b> {COOLDOWN_SECONDS / 60} мин";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: messageText,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // ВИЗУАЛЬНЫЕ ФУНКЦИИ: Вспомогательные методы
    // ============================

    private static InlineKeyboardMarkup GetHelpKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📖 Показать справку", "help"),
                InlineKeyboardButton.WithCallbackData("🔄 Проверить членство", "check_membership")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🏪 Магазин", "help_shop"),
                InlineKeyboardButton.WithCallbackData("📊 Статистика", "help_stats")
            },
            new[]
            {
                InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
            }
        });
    }

    // ============================
    // ПРОМОКОДЫ
    // ============================

    private static void LoadPromoCodes()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_promoCodesFile))
                {
                    var json = File.ReadAllText(_promoCodesFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<string, PromoCodeData>>(json)
                                 ?? new Dictionary<string, PromoCodeData>();
                    _promoCodes = new ConcurrentDictionary<string, PromoCodeData>(tempDict);
                }
                else
                {
                    _promoCodes = new ConcurrentDictionary<string, PromoCodeData>();
                }
            }
            LogInfo($"Загружено {_promoCodes.Count} промокодов");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки promocodes.json: {ex.Message}");
            _promoCodes = new ConcurrentDictionary<string, PromoCodeData>();
        }
    }

    private static void SavePromoCodes()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _promoCodes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_promoCodesFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения promocodes.json: {ex.Message}");
        }
    }

    private static async Task HandlePromoCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts == null || parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "🎫 <b>Активация промокода</b>\n\n" +
                      "Введите промокод: <code>/promo КОД</code>\n\n" +
                      "Пример: <code>/promo WELCOME</code>\n\n" +
                      "Чтобы посмотреть список промокодов (админам):\n" +
                      "<code>/listpromos</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var code = parts[1].ToUpper().Trim();

        if (!_promoCodes.ContainsKey(code))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Промокод <code>{code}</code> не найден\n\n" +
                      "Проверьте правильность написания или спросите у администратора",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var promo = _promoCodes[code];

        // Проверка срока действия
        if (promo.Expires.HasValue && DateTime.UtcNow > promo.Expires.Value)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Промокод <code>{code}</code> истёк\n" +
                      $"Срок действия: {promo.Expires.Value:dd.MM.yyyy HH:mm}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверка лимита использований
        if (promo.MaxUses > 0 && promo.UsedCount >= promo.MaxUses)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Промокод <code>{code}</code> уже использован максимальное количество раз\n" +
                      $"Использовано: {promo.UsedCount}/{promo.MaxUses}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверка, использовал ли уже этот пользователь
        if (promo.UsedBy.Contains(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Вы уже использовали промокод <code>{code}</code>\n" +
                      "Каждый промокод можно активировать только один раз",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        EnsureUserExists(userId, new User { Id = userId, Username = "", FirstName = "" });

        string resultMessage = $"✅ <b>Промокод активирован!</b>\n\n" +
                              $"🎫 Код: <code>{code}</code>\n\n" +
                              $"<b>Полученные награды:</b>\n";

        bool success = true;
        bool gotReward = false;

        // Выдача золота
        if (promo.GoldReward > 0)
        {
            var user = _users[userId];
            user.Gold += promo.GoldReward;
            _users[userId] = user;
            SaveUsers();

            resultMessage += $"💰 <b>+{promo.GoldReward} золота</b>\n";
            gotReward = true;
        }

        // Выдача карты
        if (!string.IsNullOrEmpty(promo.CardReward))
        {
            var cardId = promo.CardReward;
            if (!_userCards.ContainsKey(userId))
            {
                _userCards[userId] = new HashSet<string>();
            }

            if (_userCards[userId].Add(cardId))
            {
                // Обновляем статистику
                var user = _users[userId];
                user.TotalCards++;
                var partsCard = cardId.Split(':');
                if (partsCard.Length > 0)
                {
                    switch (partsCard[0])
                    {
                        case "common": user.CommonCards++; break;
                        case "rare": user.RareCards++; break;
                        case "epic": user.EpicCards++; break;
                        case "legendary": user.LegendaryCards++; break;
                        case "champion": user.ChampionCards++; break;
                    }
                }
                _users[userId] = user;
                SaveUsers();
                SaveUserCards();

                var cardName = GetCardName(cardId);
                var rarityInfo = GetCardRarityInfo(cardId);
                resultMessage += $"{rarityInfo.emoji} <b>Карта: {cardName}</b> ({rarityInfo.name})\n";
                gotReward = true;
            }
            else
            {
                resultMessage += $"⚠️ Карта уже была в вашей коллекции\n";
            }
        }

        if (!gotReward)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Промокод <code>{code}</code> не содержит наград\n" +
                      "Обратитесь к администратору",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Обновляем данные промокода
        promo.UsedCount++;
        promo.UsedBy.Add(userId);
        _promoCodes[code] = promo;
        SavePromoCodes();

        resultMessage += $"\n<b>Информация о промокоде:</b>\n";
        resultMessage += $"📅 Создан: {promo.Created:dd.MM.yyyy}\n";
        resultMessage += $"{(promo.Expires.HasValue ? $"⏰ Истекает: {promo.Expires.Value:dd.MM.yyyy}\n" : "")}";
        resultMessage += $"📊 Использовано: {promo.UsedCount}/{(promo.MaxUses == 0 ? "∞" : promo.MaxUses.ToString())}";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: resultMessage,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );

        LogInfo($"Пользователь {userId} активировал промокод {code}");
    }

    private static async Task HandleAddPromo(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        // Проверка на владельца или суперадмина
        if (!IsOwner(userId) && !IsSuperAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только владелец и суперадмины могут создавать промокоды",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Восстанавливаем полную команду для парсинга кавычек
        string fullCommand = string.Join(" ", parts);

        // Простой парсинг с учетом кавычек
        var parsedParts = ParseCommandWithQuotes(fullCommand);

        if (parsedParts.Length < 4)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /addpromo КОД золото использований [карта rarity:filename] [дни N]\n\n" +
                      "Примеры:\n" +
                      "/addpromo WELCOME 100 50\n" +
                      "/addpromo LEGENDARY 500 10 карта legendary:dragon.jpg\n" +
                      "/addpromo NEWYEAR 200 100 дни 7\n\n" +
                      "Для файлов с пробелами используйте кавычки:\n" +
                      "/addpromo TEST 0 1 карта exclusive:\"Элитный паровозик.jpg\"",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var code = parsedParts[1].ToUpper().Trim();

        // Проверка формата кода
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9]+$"))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Код промокода должен содержать только заглавные буквы и цифры",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (!int.TryParse(parsedParts[2], out var gold) || gold < 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверное количество золота",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (!int.TryParse(parsedParts[3], out var maxUses) || maxUses < 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверное количество использований",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        string cardReward = "";
        DateTime? expires = null;

        // Обработка дополнительных параметров
        for (int i = 4; i < parsedParts.Length; i++)
        {
            if (parsedParts[i].ToLower() == "карта" && i + 1 < parsedParts.Length)
            {
                cardReward = parsedParts[i + 1];

                // Убираем кавычки, если они есть
                cardReward = cardReward.Trim('"', '\'');

                // Проверка формата карты
                var cardParts = cardReward.Split(':');
                if (cardParts.Length != 2)
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: $"❌ Неверный формат карты. Используйте: rarity:filename\n" +
                              $"Пример: exclusive:Элитный паровозик.jpg\n" +
                              $"Или с кавычками: exclusive:\"Элитный паровозик.jpg\"",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }

                var rarity = cardParts[0].ToLower();

                // Проверяем существование редкости (включая exclusive)
                var allRarities = new List<string>(_rarities.Keys) { "evolution" };
                if (!allRarities.Contains(rarity))
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: $"❌ Неверная редкость. Доступные редкости: {string.Join(", ", allRarities)}",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }

                var fileName = cardParts[1];

                // Проверка существования папки для редкости
                if (!_imageFolders.ContainsKey(rarity))
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: $"❌ Папка для редкости {rarity} не найдена",
                        parseMode: ParseMode.Html,
                        cancellationToken: ct
                    );
                    return;
                }

                // Проверка существования файла
                var folderPath = _imageFolders[rarity];
                var fullPath = Path.Combine(folderPath, fileName);

                if (!File.Exists(fullPath))
                {
                    // Пробуем найти файл без учета регистра
                    var files = Directory.GetFiles(folderPath, "*.*")
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var matchingFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

                    if (matchingFile == null)
                    {
                        var availableFiles = string.Join(", ", files.Select(f => Path.GetFileName(f)).Take(5));
                        await bot.SendMessage(
                            chatId: chatId,
                            messageThreadId: messageThreadId,
                            text: $"❌ Файл '{fileName}' не найден в папке {rarity}.\n\n" +
                                  $"Доступные файлы (первые 5):\n{availableFiles}",
                            parseMode: ParseMode.Html,
                            cancellationToken: ct
                        );
                        return;
                    }

                    // Используем найденное имя файла
                    cardReward = $"{rarity}:{Path.GetFileName(matchingFile)}";
                }

                i++; // Пропускаем следующий элемент (сама карта)
            }
            else if (parsedParts[i].ToLower() == "дни" && i + 1 < parsedParts.Length && int.TryParse(parsedParts[i + 1], out var days))
            {
                expires = DateTime.UtcNow.AddDays(days);
                i++; // Пропускаем следующий элемент (количество дней)
            }
        }

        if (_promoCodes.ContainsKey(code))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Промокод {code} уже существует",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var promo = new PromoCodeData
        {
            Code = code,
            GoldReward = gold,
            CardReward = cardReward,
            MaxUses = maxUses,
            UsedCount = 0,
            Created = DateTime.UtcNow,
            Expires = expires,
            CreatedBy = userId,
            UsedBy = new List<long>()
        };

        _promoCodes[code] = promo;
        SavePromoCodes();

        var message = $"✅ Промокод создан!\n\n" +
                     $"🎫 Код: <code>{code}</code>\n" +
                     $"💰 Золото: {gold}\n" +
                     $"{(string.IsNullOrEmpty(cardReward) ? "" : $"🎴 Карта: {cardReward}\n")}" +
                     $"📊 Макс. использований: {(maxUses == 0 ? "∞" : maxUses.ToString())}\n" +
                     $"📅 Срок действия: {(expires.HasValue ? expires.Value.ToString("dd.MM.yyyy HH:mm") : "без срока")}\n\n" +
                     $"Пользователь может активировать командой:\n" +
                     $"/promo {code}";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );

        LogInfo($"Админ {userId} создал промокод {code}: {gold} золота, карта: {cardReward}");
    }

    // Добавьте этот вспомогательный метод в класс Program
    private static string[] ParseCommandWithQuotes(string input)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var currentPart = new StringBuilder();

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
                // Не добавляем кавычку в результат
            }
            else if (c == ' ' && !inQuotes)
            {
                if (currentPart.Length > 0)
                {
                    result.Add(currentPart.ToString());
                    currentPart.Clear();
                }
            }
            else
            {
                currentPart.Append(c);
            }
        }

        if (currentPart.Length > 0)
        {
            result.Add(currentPart.ToString());
        }

        return result.ToArray();
    }

    private static async Task HandleRemovePromo(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        // Проверка на владельца
        if (!IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только владелец бота может удалять промокоды",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /removepromo <code>КОД</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var code = parts[1].ToUpper().Trim();

        var removed = _promoCodes.TryRemove(code, out _);
        if (removed)
        {
            SavePromoCodes();
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"✅ Промокод <code>{code}</code> удалён",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            LogInfo($"Админ {userId} удалил промокод {code}");
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Не удалось удалить промокод",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleListPromos(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // Проверка на админа
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет прав для просмотра этого списка",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var promos = _promoCodes.Values.OrderBy(p => p.Created).ToList();

        if (promos.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "📋 Промокодов нет",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var message = $"📋 <b>Список промокодов ({promos.Count})</b>\n\n";

        foreach (var promo in promos.Take(10))
        {
            var status = promo.MaxUses > 0 && promo.UsedCount >= promo.MaxUses ? "🔴" :
                        promo.Expires.HasValue && DateTime.UtcNow > promo.Expires.Value ? "⏰" : "🟢";

            message += $"{status} <b>{promo.Code}</b>\n" +
                      $"💰 {promo.GoldReward} золота\n" +
                      $"{(string.IsNullOrEmpty(promo.CardReward) ? "" : $"🎴 {promo.CardReward}\n")}" +
                      $"📊 {promo.UsedCount}/{(promo.MaxUses == 0 ? "∞" : promo.MaxUses.ToString())} использований\n" +
                      $"📅 {(promo.Expires.HasValue ? $"до {promo.Expires.Value:dd.MM.yyyy}" : "без срока")}\n\n";
        }

        if (promos.Count > 10)
        {
            message += $"... и ещё {promos.Count - 10} промокодов\n";
        }

        message += "\n<b>Команды:</b>\n" +
                  "/addpromo КОД золото использований [карта rarity:filename] [дни N]\n" +
                  "/removepromo КОД\n";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleAddEvolution(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (!IsSuperAdmin(userId) && !IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только суперадмины и владелец могут создавать эволюции",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Здесь можно добавить логику создания эволюции через админ-панель
        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: "Функция создания эволюций через админ-панель будет добавлена позже.\n\n" +
                  "Пока вы можете редактировать файл evolutions.json напрямую.",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleListEvolutions(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        if (!IsAdmin(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет прав для просмотра этого списка",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var message = "📋 <b>СПИСОК ВСЕХ ЭВОЛЮЦИЙ</b>\n\n";

        foreach (var evo in _evolutions.Values.OrderBy(e => e.EvolutionName))
        {
            message += $"📦 <b>{evo.EvolutionName}</b>\n";
            message += $"   🔹 Базовая карта: {evo.GetBaseCardName()}\n";
            message += $"   🔹 Требуется: {evo.RequiredCount} карт\n";
            message += $"   🔹 Множитель: x{evo.RewardMultiplier}\n\n";
        }

        message += $"<b>Всего эволюций:</b> {_evolutions.Count}";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleRemoveEvolution(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (!IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только владелец может удалять эволюции",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /removeevolution <название_эволюции>\n\n" +
                      "Пример: /removeevolution Армия скелетов (эво)",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var evoName = string.Join(" ", parts.Skip(1)).Trim();

        if (string.IsNullOrEmpty(evoName))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Название эволюции не может быть пустым",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Ищем эволюцию (частичное совпадение)
        var matchingEvolutions = _evolutions.Keys
            .Where(k => k.Contains(evoName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingEvolutions.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Эволюция '{evoName}' не найдена",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (matchingEvolutions.Count > 1)
        {
            var message = $"🔍 Найдено несколько эволюций:\n\n";
            for (int i = 0; i < matchingEvolutions.Count; i++)
            {
                message += $"{i + 1}. {matchingEvolutions[i]}\n";
            }
            message += "\nУточните название или используйте точное название в кавычках";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        string exactEvoName = matchingEvolutions[0];

        // Удаляем из памяти
        if (_evolutions.TryRemove(exactEvoName, out var removedEvo))
        {
            // Обновляем JSON файл
            try
            {
                var wrapper = new EvolutionListWrapper
                {
                    Evolutions = _evolutions.Values.ToList()
                };

                string json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
                File.WriteAllText(_evolutionsFile, json, Encoding.UTF8);

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Эволюция <b>{exactEvoName}</b> удалена из файла.\n\n" +
                          $"Базовая карта: {removedEvo.GetBaseCardName()}\n" +
                          $"Требовалось карт: {removedEvo.RequiredCount}\n\n" +
                          $"⚠️ <i>Изменения вступят в силу немедленно.</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );

                LogInfo($"Админ {userId} удалил эволюцию {exactEvoName}");
            }
            catch (Exception ex)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Эволюция удалена из памяти, но не удалось обновить файл: {ex.Message}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Не удалось удалить эволюцию",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleResetEvolutions(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (!IsOwner(userId))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Только владелец может сбрасывать эволюции пользователей",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /resetevolutions <user_id> или @username\n\n" +
                      "Примеры:\n" +
                      "/resetevolutions 123456789\n" +
                      "/resetevolutions @username\n\n" +
                      "Или для всех пользователей:\n" +
                      "/resetevolutions all",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        try
        {
            if (parts[1].ToLower() == "all")
            {
                // Сброс для всех пользователей
                int count = _userEvolutions.Count;
                _userEvolutions.Clear();
                SaveUserEvolutions();

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Эволюции сброшены для ВСЕХ пользователей ({count} записей)",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );

                LogInfo($"Админ {userId} сбросил эволюции для всех пользователей");
                return;
            }

            var (targetUserId, userInfo) = await ResolveUserWithInfo(bot, parts[1], ct);

            if (_userEvolutions.ContainsKey(targetUserId))
            {
                var userEvo = _userEvolutions[targetUserId];
                int evoCount = userEvo.EvolutionsObtained.Count(kvp => kvp.Value);

                _userEvolutions.TryRemove(targetUserId, out _);
                SaveUserEvolutions();

                string displayName = userInfo?.FirstName ?? "Пользователь";
                string usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"✅ Эволюции сброшены для пользователя {displayName}{usernameDisplay}\n" +
                          $"🆔 ID: <code>{targetUserId}</code>\n" +
                          $"📊 Было эволюций: {evoCount}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );

                LogInfo($"Админ {userId} сбросил эволюции пользователя {targetUserId}");
            }
            else
            {
                string displayName = userInfo?.FirstName ?? "Пользователь";
                string usernameDisplay = !string.IsNullOrEmpty(userInfo?.Username) ? $" (@{userInfo.Username})" : "";

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"ℹ️ У пользователя {displayName}{usernameDisplay} / <code>{targetUserId}</code> нет эволюций.",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
            }
        }
        catch (ArgumentException ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // ПОМОЩЬ
    // ============================

    private static async Task SendHelpMenu(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, int? messageId = null, CancellationToken ct = default)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
            new[]
            {
                InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "check_membership")
            }
        });

            var message = MessageManager.Get("access_denied", missingText);

            if (messageId.HasValue)
            {
                try
                {
                    await bot.EditMessageText(
                        chatId: chatId,
                        messageId: (int)messageId,
                        text: message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: ct
                    );
                }
                catch (ApiRequestException)
                {
                    // Если не удалось отредактировать, отправляем новое сообщение
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: ct
                    );
                }
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: message,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
            }
            return;
        }

    var keyboardMenu = new InlineKeyboardMarkup(new[]
{
    new[]
    {
        InlineKeyboardButton.WithCallbackData("🎴 Карты и колоды", "help_cards"),
        InlineKeyboardButton.WithCallbackData("💰 Экономика", "help_economy")
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("🏪 Магазин ящиков", "help_shop"),
        InlineKeyboardButton.WithCallbackData("🌟 Эволюции", "help_evolutions") // НОВАЯ КНОПКА
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("🔍 Просмотр карт", "help_viewcards"),
        InlineKeyboardButton.WithCallbackData("⚙️ Команды", "help_commands")
    },
    new[]
    {
        InlineKeyboardButton.WithCallbackData("❓ Как играть", "help_howto"),
        InlineKeyboardButton.WithCallbackData("🎫 Промокоды", "help_promo")
    }
});

        var messageText = "🆘 <b>ПОМОЩЬ ПО БОТУ \"ПИРОЖКИ\"</b>\n\n"
                        + "Добро пожаловать в игру по сбору коллекции карт!\n"
                        + "Выберите раздел справки с помощью кнопок ниже:\n\n"
                        + "<i>ℹ️ Нажимайте на кнопки, чтобы получить подробную информацию по каждому разделу</i>";

        if (messageId.HasValue)
        {
            try
            {
                await bot.EditMessageText(
                    chatId: chatId,
                    messageId: (int)messageId,
                    text: messageText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboardMenu,
                    cancellationToken: ct
                );
            }
            catch (ApiRequestException)
            {
                // Если не удалось отредактировать, отправляем новое сообщение
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: messageText,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboardMenu,
                    cancellationToken: ct
                );
            }
        }
        else
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: messageText,
                parseMode: ParseMode.Html,
                replyMarkup: keyboardMenu,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // ОБНОВЛЕННЫЙ ОБРАБОТЧИК CALLBACK ДЛЯ ПОМОЩИ С МАГАЗИНОМ
    // ============================

    private static async Task HandleHelpCallbackQuery(ITelegramBotClient bot, CallbackQuery callbackQuery, int? messageThreadId, CancellationToken ct)
    {
        var chatId = callbackQuery.Message.Chat.Id;
        var userId = callbackQuery.From.Id;
        var data = callbackQuery.Data;
        var messageId = callbackQuery.Message.MessageId;

        var threadId = messageThreadId ?? callbackQuery.Message.MessageThreadId;

        try
        {
            string message = "";
            InlineKeyboardMarkup keyboard = null;

            switch (data)
            {
                case "help_shop":
                    message = "🏪 <b>МАГАЗИН ЯЩИКОВ</b>\n\n"
                            + "<b>Основные команды:</b>\n"
                            + "• /shop - открыть магазин ящиков\n"
                            + "• /buy wooden - купить деревянный ящик\n"
                            + "• /buy iron - купить железный ящик\n"
                            + "• /buy golden - купить золотой ящик\n"
                            + "• /convert 100 - обменять 100 золота на 10 гемов\n"
                            + "• /gems - показать баланс гемов\n\n"

                            + "<b>🎯 Курс обмена:</b>\n"
                            + "10 золота = 1 гем\n"
                            + "Минимальная сумма: 10 золота\n\n"

                            + "<b>📦 Типы ящиков:</b>\n"
                            + "• <b>Деревянный ящик</b> (10 гемов):\n"
                            + "  🎴 1-2 карты\n"
                            + "  📊 Базовые шансы\n\n"

                            + "• <b>Железный ящик</b> (25 гемов):\n"
                            + "  🎴 2-3 карты\n"
                            + "  📊 Улучшенные шансы\n\n"

                            + "• <b>Золотой ящик</b> (50 гемов):\n"
                            + "  🎴 2-4 карты\n"
                            + "  📊 Лучшие шансы\n\n"

                            + "<b>⚡ Преимущества ящиков:</b>\n"
                            + "• Можно получить несколько карт сразу\n"
                            + "• Улучшенные шансы на редкие карты\n"
                            + "• Нет кулдауна на открытие ящиков\n"
                            + "• Дубликаты дают 25% золота\n\n"

                            + "<i>Используйте /shop для просмотра магазина</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                            InlineKeyboardButton.WithCallbackData("💰 Экономика", "help_economy")
                        }
                    });
                    break;

                case "help_economy":
                    message = "💰 <b>ЭКОНОМИКА И НАГРАДЫ</b>\n\n"
                            + "<b>Получение золота:</b>\n"
                            + "• За новую карту: полная стоимость\n"
                            + "• За дубликат: 25% от стоимости\n\n"

                            + "<b>Стоимость карт:</b>\n"
                            + "🔵 Обычная: 10 золота\n"
                            + "🟠 Редкая: 25 золота\n"
                            + "🟣 Эпическая: 50 золота\n"
                            + "⚪ Легендарная: 100 золота\n"
                            + "🌟 Чемпионская: 200 золота\n\n"

                            + "<b>🏪 Магазин ящиков:</b>\n"
                            + "• Золото можно конвертировать в гемы\n"
                            + "• Гемы используются для покупки ящиков\n"
                            + "• Ящики дают несколько карт за раз\n\n"

                            + "<b>Команды:</b>\n"
                            + "• /gold или /balance - посмотреть баланс\n"
                            + "• /shop - открыть магазин\n"
                            + "• /promo КОД - активировать промокод\n\n"

                            + "<i>Используйте /shop для покупки ящиков</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                            InlineKeyboardButton.WithCallbackData("🎴 Карты", "help_cards")
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("🏪 Магазин", "help_shop")
                        }
                    });
                    break;

                case "help_cards":
                    message = "🎴 <b>КАРТЫ И КОЛОДЫ</b>\n\n"
                            + "<b>Основные команды:</b>\n"
                            + "• /card или /pic - открыть случайную карту\n"
                            + "• /mycards - посмотреть свою коллекцию\n"
                            + "• /deck - управление боевой колодой\n"
                            + "• /shop - купить ящик с картами\n"
                            + "• /cards - просмотр карт по редкостям\n"  // ДОБАВЛЕНО
                            + "• /view - поиск карты по названию\n\n"     // ДОБАВЛЕНО

                            + "<b>Управление колодой:</b>\n"
                            + "• /deck info - показать колоду\n"
                            + "• /deck list - показать коллекцию\n"
                            + "• /deck add название - добавить карту\n"
                            + "• /deck remove название - удалить карту\n"
                            + "• /deck clear - очистить колоду\n"
                            + "• /deck favorite - любимая карта\n\n"

                            + "<b>Просмотр карт:</b>\n"
                            + "• /cards - меню редкостей\n"
                            + "• /cards редкость - карты определенной редкости\n"
                            + "• /view название - поиск карты\n"
                            + "• Можно видеть, есть ли карта в коллекции\n\n"

                            + "<b>Получение карт:</b>\n"
                            + "• Обычный способ: /card (кулдаун 20 мин)\n"
                            + "• Ящики: /shop (3 ящика в час)\n"
                            + "• Промокоды: /promo\n\n"

                            + "<b>Редкости карт:</b>\n"
                            + "🔵 Обычная (10 золота)\n"
                            + "🟠 Редкая (25 золота)\n"
                            + "🟣 Эпическая (50 золота)\n"
                            + "⚪ Легендарная (100 золота)\n"
                            + "🌟 Чемпионская (200 золота)\n"
                            + "🔮 Эволюционная (x2 золота от редкости)\n"
                            + "💜 Эксклюзивная (0 золота, только от администрации)\n\n"

                            + "<i>Максимальный размер колоды: 8 карт</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                    InlineKeyboardButton.WithCallbackData("💰 Экономика", "help_economy")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔍 Просмотр карт", "help_viewcards"), // ДОБАВЛЕНО
                    InlineKeyboardButton.WithCallbackData("🏪 Магазин", "help_shop")
                }
            });
                    break;

                case "help_stats":
                    message = "📊 <b>СТАТИСТИКА И РЕЙТИНГИ</b>\n\n"
                            + "<b>Команды статистики:</b>\n"
                            + "• /stats - ваша статистика\n"
                            + "• /top - меню топов игроков\n"
                            + "• /top_money - топ по золоту\n"
                            + "• /top_card - топ по картам\n"
                            + "• /profile - ваш профиль\n"
                            + "• /cooldown или /cd - время до след. карты\n\n"

                            + "<b>Типы топов:</b>\n"
                            + "• 🥇 <b>Топ по золоту</b> - кто заработал больше всего золота\n"
                            + "• 🎴 <b>Топ по картам</b> - у кого самая большая коллекция\n\n"

                            + "<b>Как получить золото:</b>\n"
                            + "• Открывайте карты командой /card\n"
                            + "• Покупайте ящики в /shop\n"
                            + "• Новые карты дают полную стоимость\n"
                            + "• Дубликаты дают 25% от стоимости\n\n"

                                            + "<b>Профиль игрока:</b>\n"
                            + "• Показывает золото, гемы и карты\n"
                            + "• Статистику по редкостям\n"
                            + "• Любимую карту и колоду\n"
                            + "• Дата регистрации и активность\n\n"

                            + "<i>Топ обновляется в реальном времени</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                            InlineKeyboardButton.WithCallbackData("⚙️ Команды", "help_commands")
                        }
                    });
                    break;

                case "help_commands":
                    message = "⚙️ <b>ВСЕ КОМАНДЫ</b>\n\n"
                            + "<b>Основные команды:</b>\n"
                            + "• /start - начало работы с ботом\n"
                            + "• /help - это меню помощи\n"
                            + "• /card или /pic - открыть карту\n"
                            + "• /top - топ игроков\n\n"

                            + "<b>Статистика:</b>\n"
                            + "• /profile - профиль игрока\n"
                            + "• /stats - детальная статистика\n"
                            + "• /mycards - коллекция карт\n"
                            + "• /gold или /balance - баланс\n"
                            + "• /gems - баланс гемов\n"
                            + "• /top_money - топ по золоту\n"
                            + "• /top_card - топ по картам\n"
                            + "• /cooldown или /cd - кулдаун\n\n"

                            + "<b>🔍 Просмотр карт:</b>\n"  // НОВЫЙ РАЗДЕЛ
                            + "• /cards - меню редкостей\n"
                            + "• /cards редкость - карты редкости\n"
                            + "• /cards редкость номер - конкретная карта\n"
                            + "• /view или /showcard - поиск по названию\n\n"

                            + "<b>Колода:</b>\n"
                            + "• /deck - управление колодой\n"
                            + "• /deck info - информация о колоде\n"
                            + "• /deck list - коллекция карт\n"
                            + "• /deck add - добавить в колоду\n"
                            + "• /deck remove - удалить из колоды\n\n"

                            + "<b>🏪 Магазин ящиков:</b>\n"
                            + "• /shop - открыть магазин\n"
                            + "• /buy wooden - деревянный ящик\n"
                            + "• /buy iron - железный ящик\n"
                            + "• /buy golden - золотой ящик\n"
                            + "• /convert 100 - обменять золото\n\n"

                            + "<b>Дополнительно:</b>\n"
                            + "• /chance или /prob - шансы выпадения\n"
                            + "• /promo КОД - активировать промокод\n"
                            + "• /chestlimit - лимит сундуков";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                    InlineKeyboardButton.WithCallbackData("📊 Статистика", "help_stats")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔍 Просмотр карт", "help_viewcards"), // ДОБАВЛЕНО
                    InlineKeyboardButton.WithCallbackData("🏪 Магазин", "help_shop")
                }
            });
                    break;

                case "help_howto":
                    message = "❓ <b>КАК ИГРАТЬ</b>\n\n"
                            + "<b>1. Подписка</b>\n"
                            + "• Подпишитесь на группу и канал\n"
                            + "• Используйте команды в личных сообщениях или в установленном топике\n\n"

                            + "<b>2. Сбор карт</b>\n"
                            + "• Используйте /card каждые 20 минут\n"
                            + "• Используйте /shop для покупки ящиков\n"
                            + "• Собирайте разные редкости\n"
                            + "• Получайте золото за карты\n\n"

                            + "<b>3. 🔍 Просмотр карт</b>\n"  // ДОБАВЛЕНО
                            + "• Используйте /cards для просмотра по редкостям\n"
                            + "• Используйте /view для поиска карт по названию\n"
                            + "• Смотрите, какие карты у вас уже есть\n"
                            + "• Узнавайте стоимость и характеристики карт\n\n"

                            + "<b>4. Экономика</b>\n"
                            + "• Золото можно обменять на гемы\n"
                            + "• Гемы нужны для покупки ящиков\n"
                            + "• Ящики дают несколько карт сразу\n"
                            + "• У ящиков улучшенные шансы\n\n"

                            + "<b>5. Создание колоды</b>\n"
                            + "• Добавляйте карты в колоду (/deck add)\n"
                            + "• Максимум 8 карт в колоде\n"
                            + "• Выбирайте любимую карту\n\n"

                            + "<b>6. Прогресс</b>\n"
                            + "• Собирайте коллекцию\n"
                            + "• Зарабатывайте золото и гемы\n"
                            + "• Соревнуйтесь в топе\n"
                            + "• Открывайте ящики с лучшими шансами\n\n"

                            + "<b>7. Сообщество</b>\n"
                            + "• Общайтесь в личных сообщениях с ботом\n"
                            + "• Участвуйте в розыгрышах\n"
                            + "• Следите за обновлениями\n\n"

                            + "<i>Удачи в сборе коллекции!</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                    InlineKeyboardButton.WithCallbackData("🎫 Промокоды", "help_promo")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔍 Просмотр карт", "help_viewcards"), // ДОБАВЛЕНО
                    InlineKeyboardButton.WithCallbackData("🏪 Магазин", "help_shop")
                }
            });
                    break;

                case "help_promo":
                    message = "🎫 <b>ПРОМОКОДЫ</b>\n\n"
                            + "<b>Как активировать:</b>\n"
                            + "• Используйте команду /promo КОД\n"
                            + "• Пример: /promo WELCOME\n\n"

                            + "<b>Что можно получить:</b>\n"
                            + "• Золото для покупок\n"
                            + "• Гемы для магазина\n"
                            + "• Уникальные карты\n"
                            + "• Редкие редкости\n\n"

                            + "<b>Где брать промокоды:</b>\n"
                            + "• Розыгрыши в группе\n"
                            + "• Специальные события\n"
                            + "• Награды от администраторов\n"
                            + "• Новые обновления\n\n"

                            + "<b>Ограничения:</b>\n"
                            + "• Один промокод = одно использование\n"
                            + "• Могут быть ограничены по времени\n"
                            + "• Лимит на количество активаций\n\n"

                            + "<b>🏪 Ящики vs Промокоды:</b>\n"
                            + "• Ящики - за гемы (получаются из золота)\n"
                            + "• Промокоды - бесплатные награды\n"
                            + "• И то и другое помогает собрать коллекцию\n\n"

                            + "<i>Следите за анонсами в канале!</i>";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                            InlineKeyboardButton.WithCallbackData("❓ Как играть", "help_howto")
                        }
                    });
                    break;

                case "help_back":
                    await SendHelpMenu(
                        bot,
                        chatId,
                        userId,
                        callbackQuery.Message?.MessageThreadId,
                        messageId,
                        ct
                    );
                    return;

                case "help_viewcards":
                    message = "🔍 <b>ПРОСМОТР КАРТ И ПОИСК</b>\n\n"
                            + "<b>Просмотр карт по редкостям:</b>\n"
                            + "• /cards - меню выбора редкости\n"
                            + "• /cards common - обычные карты\n"
                            + "• /cards rare - редкие карты\n"
                            + "• /cards epic - эпические карты\n"
                            + "• /cards legendary - легендарные карты\n"
                            + "• /cards champion - чемпионские карты\n\n"

                            + "<b>Просмотр конкретной карты:</b>\n"
                            + "• /cards epic 3 - 3-я эпическая карта\n"
                            + "• /cards rare 5 - 5-я редкая карта\n"
                            + "• /view дракон - поиск по названию\n"
                            + "• /showcard огненный - поиск по названию\n\n"

                            + "<b>💡 Особенности просмотра:</b>\n"
                            + "• Видно, есть ли карта у вас в коллекции (✅/❌)\n"
                            + "• Показывается стоимость карты в золоте\n"
                            + "• Можно сразу добавить карту в колоду\n"
                            + "• Показывается ID карты для админ-команд\n\n"

                            + "<b>Примеры использования:</b>\n"
                            + "<code>/cards</code> - выбрать редкость\n"
                            + "<code>/cards epic</code> - список эпических карт\n"
                            + "<code>/cards rare 2</code> - 2-я редкая карта\n"
                            + "<code>/view ледяной</code> - поиск по слову 'ледяной'\n"
                            + "<code>/showcard мухтар</code> - точное название";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
                    InlineKeyboardButton.WithCallbackData("🎴 Карты", "help_cards")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📊 Статистика", "help_stats")
                }
            });
                    break;

                case "help_evolutions":
                    message = "🌟 <b>ЭВОЛЮЦИИ КАРТ</b>\n\n"
                            + "<b>Как работают эволюции?</b>\n\n"
                            + "• Соберите <b>6 одинаковых карт</b>, чтобы получить эволюцию\n"
                            + "• Эволюция - это отдельная карта с улучшенными свойствами\n"
                            + "• Базовая карта остается в коллекции\n"
                            + "• Эволюционировавшая карта дает в <b>2 раза больше золота</b>\n\n"

                            + "<b>Преимущества эволюций:</b>\n"
                            + "• Удвоенное золото за дубликаты\n"
                            + "• Уникальный значок в коллекции\n"
                            + "• Отдельная карта в профиле\n\n"

                            + "<b>Команды:</b>\n"
                            + "• /myevolutions - показать ваши эволюции\n"
                            + "• /evolutions - то же самое\n\n"

                            + "<b>Пример:</b>\n"
                            + "Соберите 6 карт 'Рыцарь' → получите 'Рыцарь (эво)'\n"
                            + "Теперь каждый новый 'Рыцарь' дает 20 золота вместо 10!";

                    keyboard = new InlineKeyboardMarkup(new[]
                    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "help_back"),
            InlineKeyboardButton.WithCallbackData("🎴 Карты", "help_cards")
        }
    });
                    break;




                default:
                    return;
            }

            // Всегда редактируем текущее сообщение
            await bot.EditMessageText(
                  chatId: chatId,
                  messageId: (int)messageId,
                  text: message,
                  parseMode: ParseMode.Html,
                  replyMarkup: keyboard,
                  cancellationToken: ct
        );
        }
        catch (Exception ex)
        {
            LogError($"Ошибка обработки help callback: {ex.Message}");
        }
    }

    // ============================
    // УТИЛИТЫ
    // ============================

    // Добавьте этот метод в класс Program, например в разделе УТИЛИТЫ:

    private static int CalculateTotalGoldSpent(long userId)
    {
        if (!_users.ContainsKey(userId))
            return 0;

        var user = _users[userId];
        int totalGoldSpent = 0;

        // Считаем, сколько золота было потрачено из всего заработанного
        // totalGoldSpent = totalGoldEarned - currentGold
        // Где totalGoldEarned можно посчитать по количеству открытых карт

        // Рассчитываем сколько золота было заработано всего
        int totalGoldEarned = 0;

        // За обычные карты
        totalGoldEarned += user.CommonCards * 10;

        // За редкие карты (полная стоимость за новые, 25% за дубликаты)
        // Это приблизительный расчет, так как точные данные о дубликатах не хранятся
        totalGoldEarned += user.RareCards * 25;

        // За эпические карты
        totalGoldEarned += user.EpicCards * 50;

        // За легендарные карты
        totalGoldEarned += user.LegendaryCards * 100;

        // За чемпионские карты
        totalGoldEarned += user.ChampionCards * 200;

        // Вычисляем потраченное золото
        totalGoldSpent = totalGoldEarned - user.Gold;

        // Если результат отрицательный, значит где-то ошибка в расчетах
        // или золото было добавлено админом
        if (totalGoldSpent < 0)
            totalGoldSpent = 0;

        return totalGoldSpent;
    }

    private static string GetRandomRarity()
    {
        const double COMMON_PERCENT = 64.8;
        const double RARE_PERCENT = 25.2;
        const double EPIC_PERCENT = 7.6;
        const double LEGENDARY_PERCENT = 2.2;
        const double CHAMPION_PERCENT = 0.2;

        var roll = _random.NextDouble() * 100.0;

        double current = 0;

        current += COMMON_PERCENT;
        if (roll < current) return "common";

        current += RARE_PERCENT;
        if (roll < current) return "rare";

        current += EPIC_PERCENT;
        if (roll < current) return "epic";

        current += LEGENDARY_PERCENT;
        if (roll < current) return "legendary";

        return "champion";
    }

    private static double GetPercentage(int count, int total)
    {
        if (total == 0) return 0;
        return Math.Round((double)count / total * 100, 1);
    }

    private static async Task CheckMembershipAndRespond(ITelegramBotClient bot, int? messageThreadId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var userId = callbackQuery.From.Id;
        var chatId = callbackQuery.Message.Chat.Id;
        var messageId = callbackQuery.Message.MessageId;

        // Проверка топика для callback'ов в группах
        if (chatId < 0) // Это группа
        {
            var topicId = GetTopicIdForGroup(chatId);
            if (topicId.HasValue && messageThreadId != topicId.Value)
            {
                // Игнорируем callback не из нужного топика
                return;
            }
        }

        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (isChannelMember)
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: (int)messageId,
                text: MessageManager.Get("check_membership_success"),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                InlineKeyboardButton.WithCallbackData("🎯 Начать играть", "help")
                }),
                cancellationToken: ct
            );
        }
        else
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.EditMessageText(
                chatId: chatId,
                messageId: (int)messageId,
                text: MessageManager.Get("check_membership_fail", missingText),
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                new[]
                {
                    InlineKeyboardButton.WithUrl("📺 Подписаться на канал", _channelLink)
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Проверить снова", "check_membership")
                }
                }),
                cancellationToken: ct
            );
        }
    }

    // ============================
    // СИСТЕМНЫЕ ФУНКЦИИ: Загрузка и сохранение гемов
    // ============================

    private static void LoadUserGems()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_userGemsFile))
                {
                    var json = File.ReadAllText(_userGemsFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, int>>(json)
                                 ?? new Dictionary<long, int>();

                    _userGems = new ConcurrentDictionary<long, int>(tempDict);
                }
                else
                {
                    _userGems = new ConcurrentDictionary<long, int>();
                }
            }
            LogInfo($"Загружено {_userGems.Count} записей о гемах пользователей");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки usergems.json: {ex.Message}");
            _userGems = new ConcurrentDictionary<long, int>();
        }
    }

    private static void SaveUserGems()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _userGems.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_userGemsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения usergems.json: {ex.Message}");
        }
    }

    // ============================
    // КОМАНДА МАГАЗИНА (/shop)
    // ============================

    private static async Task SendShopMenu(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }


    var userGold = _users.ContainsKey(userId) ? _users[userId].Gold : 0;
        var userGems = _userGems.ContainsKey(userId) ? _userGems[userId] : 0;
        var chestLimitInfo = GetChestLimitInfo(userId); // Переименовано
        var openedCountVar = chestLimitInfo.openedCount; // Переименовано
        var limitVar = chestLimitInfo.limit; // Переименовано
        var timeLeft = chestLimitInfo.timeLeft;
        var remainingChests = limitVar - openedCountVar;

        var message = "<b>🏪 МАГАЗИН ЯЩИКОВ</b>\n\n";
        message += $"💰 <b>Ваше золото:</b> <code>{userGold}</code>\n";
        message += $"💎 <b>Ваши гемы:</b> <code>{userGems}</code>\n";
        message += $"📦 <b>Лимит сундуков:</b> <code>{openedCountVar}/{limitVar}</code>\n";
        message += $"🎯 <b>Доступно сундуков:</b> <code>{remainingChests}</code>\n";

        if (timeLeft != "0 минут")
        {
            message += $"🔄 <b>До сброса лимита:</b> <code>{timeLeft}</code>\n";
        }

        message += $"📊 <b>Курс обмена:</b> 10 золота = 1 гем\n\n";

        message += "<b>📦 ДОСТУПНЫЕ ЯЩИКИ:</b>\n\n";

        message += $"<b>1. Деревянный ящик</b>\n";
        message += $"   💰 Стоимость: <code>{WOODEN_CHEST_PRICE}</code> гемов (<code>{WOODEN_CHEST_PRICE * 10}</code> золота)\n";
        message += $"   🎴 Карт в ящике: <b>1-2</b> карты\n";
        message += $"   📊 Шансы: Обычные 60% | Редкие 30% | Эпические 7.5% | Легендарные 2% | Чемпионские 0.5%\n\n";

        message += $"<b>2. Железный ящик</b>\n";
        message += $"   💰 Стоимость: <code>{IRON_CHEST_PRICE}</code> гемов (<code>{IRON_CHEST_PRICE * 10}</code> золота)\n";
        message += $"   🎴 Карт в ящике: <b>2-3</b> карты\n";
        message += $"   📊 Шансы: Обычные 55% | Редкие 30% | Эпические 13% | Легендарные 4% | Чемпионские 1%\n\n";

        message += $"<b>3. Золотой ящик</b>\n";
        message += $"   💰 Стоимость: <code>{GOLDEN_CHEST_PRICE}</code> гемов (<code>{GOLDEN_CHEST_PRICE * 10}</code> золота)\n";
        message += $"   🎴 Карт в ящике: <b>2-4</b> карты\n";
        message += $"   📊 Шансы: Обычные 46% | Редкие 27.5% | Эпические 15% | Легендарные 8% | Чемпионские 3.5%\n\n";

        message += $"<b>⚡ ПРАВИЛА ЛИМИТА:</b>\n";
        message += $"• Максимум <b>{limitVar}</b> сундуков в час\n";
        message += $"• Лимит сбрасывается каждый час\n";
        message += $"• Все типы сундуков учитываются\n\n";

        message += "<b>⚡ КОМАНДЫ:</b>\n";
        message += "<code>/buy wooden</code> - купить деревянный ящик\n";
        message += "<code>/buy iron</code> - купить железный ящик\n";
        message += "<code>/buy golden</code> - купить золотой ящик\n";
        message += "<code>/convert 100</code> - обменять 100 золота на 10 гемов\n";
        message += "<code>/gems</code> - показать баланс гемов\n";
        message += "<code>/chestlimit</code> - информация о лимите";

        var keyboard = new InlineKeyboardMarkup(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📦 Деревянный", "buy_wooden"),
            InlineKeyboardButton.WithCallbackData("⚙️ Железный", "buy_iron"),
            InlineKeyboardButton.WithCallbackData("💰 Золотой", "buy_golden")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("💎 Обменять золото", "convert_gold"),
            InlineKeyboardButton.WithCallbackData("📊 Лимит", "chest_limit")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 Обновить", "refresh_shop"),
            InlineKeyboardButton.WithCallbackData("💰 Баланс", "show_balance")
        }
    });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId, // Добавлен
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    // ============================
    // КОМАНДА ПОКУПКИ ЯЩИКА (/buy)
    // ============================

    private static async Task HandleBuyCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /buy <тип_ящика>\n\n" +
                      "Доступные ящики:\n" +
                      "• wooden - деревянный ящик\n" +
                      "• iron - железный ящик\n" +
                      "• golden - золотой ящик\n\n" +
                      "Пример: /buy wooden",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var chestType = parts[1].ToLower();

        if (!_chestRarityChances.ContainsKey(chestType))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверный тип ящика. Используйте: wooden, iron или golden",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        await OpenChest(bot, chatId, userId, chestType, messageThreadId, ct);
    }

    // ============================
    // ОТКРЫТИЕ ЯЩИКА
    // ============================

    private static async Task OpenChest(ITelegramBotClient bot, long chatId, long userId, string chestType, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверка лимита сундуков
        if (!CanOpenChest(userId))
        {
            var chestInfo = GetChestLimitInfo(userId);
            var openedCountInfo = chestInfo.openedCount; // Переименовано
            var limitInfo = chestInfo.limit; // Переименовано
            var timeLeftInfo = chestInfo.timeLeft; // Переименовано для консистентности

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"⏰ <b>Достигнут лимит сундуков!</b>\n\n" +
                      $"📊 Использовано сундуков: <code>{openedCountInfo}/{limitInfo}</code>\n" +
                      $"🕐 До сброса лимита: <code>{timeLeftInfo}</code>\n\n" +
                      $"Лимит сундуков: <b>{limitInfo} сундуков в час</b>\n" +
                      $"⌛ Лимит сбрасывается каждый час\n\n" +
                      $"💡 <i>Попробуйте через {timeLeftInfo} минут</i>\n\n" +
                      $"Используйте команду <code>/chestlimit</code> для проверки лимита",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Определяем цену ящика
        int chestPrice = chestType switch
        {
            "wooden" => WOODEN_CHEST_PRICE,
            "iron" => IRON_CHEST_PRICE,
            "golden" => GOLDEN_CHEST_PRICE,
            _ => 0
        };

        // Проверяем баланс гемов
        if (!_userGems.ContainsKey(userId) || _userGems[userId] < chestPrice)
        {
            var neededGems = chestPrice - (_userGems.ContainsKey(userId) ? _userGems[userId] : 0);
            var neededGold = neededGems * 10;

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Недостаточно гемов для покупки ящика!\n\n" +
                      $"💰 Стоимость ящика: <code>{chestPrice}</code> гемов\n" +
                      $"💎 Ваши гемы: <code>{(_userGems.ContainsKey(userId) ? _userGems[userId] : 0)}</code>\n" +
                      $"🔄 Нужно ещё: <code>{neededGems}</code> гемов (<code>{neededGold}</code> золота)\n\n" +
                      $"Используйте команду:\n" +
                      $"<code>/convert {neededGold}</code> - чтобы обменять золото на гемы\n\n" +
                      $"Или заработайте больше золота командой <code>/card</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Регистрируем открытие сундука
        RegisterChestOpen(userId, chestType);

        // Вычитаем гемы
        _userGems[userId] -= chestPrice;
        SaveUserGems();

        // Определяем количество карт в ящике
        var cardCountRange = _chestCardCount[chestType];
        var cardCount = _random.Next(cardCountRange.min, cardCountRange.max + 1);

        var chestName = chestType switch
        {
            "wooden" => "Деревянный",
            "iron" => "Железный",
            "golden" => "Золотой",
            _ => "Неизвестный"
        };

        var chestEmoji = chestType switch
        {
            "wooden" => "📦",
            "iron" => "⚙️",
            "golden" => "💰",
            _ => "🎁"
        };

        LogInfo($"Пользователь {userId} открывает {chestName} ящик за {chestPrice} гемов, карт: {cardCount}");

        // Открываем карты
        var openedCards = new List<(string cardId, string cardName, string rarity, int goldValue, bool isDuplicate)>();
        var totalGoldEarned = 0;

        for (int i = 0; i < cardCount; i++)
        {
            var rarity = GetRandomRarityForChest(chestType);

            // Получаем случайную карту
            (string fileName, string fullPath) randomCard;
            try
            {
                randomCard = GetRandomCard(rarity);
            }
            catch (Exception ex)
            {
                LogError($"Ошибка при получении карты редкости {rarity}: {ex.Message}");
                continue;
            }

            var cardId = $"{rarity}:{randomCard.fileName}";
            var cardName = Path.GetFileNameWithoutExtension(randomCard.fileName)
                .Replace('_', ' ')
                .Replace('-', ' ');
            var goldValue = _rarities[rarity].gold;

            // Проверяем, есть ли уже эта карта у пользователя
            bool isDuplicate = _userCards.ContainsKey(userId) && _userCards[userId].Contains(cardId);
            int goldEarned = isDuplicate ? goldValue / 4 : goldValue;

            openedCards.Add((cardId, cardName, rarity, goldEarned, isDuplicate));
            totalGoldEarned += goldEarned;

            // Добавляем карту в коллекцию пользователя
            if (!isDuplicate)
            {
                if (!_userCards.ContainsKey(userId))
                {
                    _userCards[userId] = new HashSet<string>();
                }
                _userCards[userId].Add(cardId);

                // Обновляем статистику
                if (_users.ContainsKey(userId))
                {
                    var user = _users[userId];
                    user.TotalCards++;
                    switch (rarity)
                    {
                        case "common": user.CommonCards++; break;
                        case "rare": user.RareCards++; break;
                        case "epic": user.EpicCards++; break;
                        case "legendary": user.LegendaryCards++; break;
                        case "champion": user.ChampionCards++; break;
                    }
                    _users[userId] = user;
                }
            }
            else
            {
                // Проверяем эволюцию для дубликата
                if (HasEvolutionForCard(userId, cardName))
                {
                    goldEarned = goldValue / 4 * 2; // Удвоенное золото за дубликат
                }
            }

            // Добавляем золото
            if (_users.ContainsKey(userId))
            {
                var user = _users[userId];
                user.Gold += goldEarned;
                _users[userId] = user;
            }

            // Проверяем эволюции
            await CheckAndActivateEvolutions(bot, userId, cardName, messageThreadId, ct);

            // Добавляем золото пользователю
            if (_users.ContainsKey(userId))
            {
                var user = _users[userId];
                user.Gold += goldEarned;
                _users[userId] = user;
            }
        }

        // Проверяем, получили ли хоть одну карту
        if (openedCards.Count == 0)
        {
            // Возвращаем гемы если не получилось открыть ни одной карты
            _userGems[userId] += chestPrice;
            SaveUserGems();

            // Отменяем регистрацию открытия сундука
            if (_userChestStats.ContainsKey(userId))
            {
                var stats = _userChestStats[userId];
                if (stats.ChestOpens.Count > 0)
                {
                    stats.ChestOpens.RemoveAt(stats.ChestOpens.Count - 1);
                    _userChestStats[userId] = stats;
                    SaveUserChestStats();
                }
            }

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ <b>Не удалось открыть ящик!</b>\n\n" +
                      $"Приносим извинения, возникла техническая ошибка.\n" +
                      $"💎 <b>{chestPrice}</b> гемов возвращены на ваш счет.\n\n" +
                      $"Попробуйте ещё раз или обратитесь к администратору.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Сохраняем данные
        SaveUsers();
        SaveUserCards();

        // Формируем сообщение с результатами
        var userName = _users.ContainsKey(userId) ?
            (!string.IsNullOrEmpty(_users[userId].FirstName) ? _users[userId].FirstName : $"Игрок {userId}") :
            $"Игрок {userId}";

        var (openedCount, limit, timeLeftStr) = GetChestLimitInfo(userId);
        var remainingChests = limit - openedCount;

        var message = $"{chestEmoji} <b>{userName} открывает {chestName} ящик!</b>\n\n";
        message += $"💎 Потрачено гемов: <code>{chestPrice}</code>\n";
        message += $"🎴 Получено карт: <code>{openedCards.Count}</code>\n";
        message += $"💰 Заработано золота: <code>{totalGoldEarned}</code>\n";
        message += $"📦 Лимит сундуков: {openedCount}/{limit}\n";
        message += $"🎯 Осталось сундуков: {remainingChests}\n\n";

        if (timeLeftStr != "0 минут")
        {
            message += $"🔄 До сброса лимита: {timeLeftStr}\n\n";
        }

        message += $"<b>📦 СОДЕРЖИМОЕ ЯЩИКА:</b>\n\n";

        for (int i = 0; i < openedCards.Count; i++)
        {
            var card = openedCards[i];
            var rarityInfo = _rarities[card.rarity];
            var duplicateText = card.isDuplicate ? " (дубликат)" : "";
            var duplicateIcon = card.isDuplicate ? "🔄 " : "";

            message += $"{i + 1}. {duplicateIcon}{rarityInfo.emoji} <b>{card.cardName}</b> ({rarityInfo.name}){duplicateText}\n";
            message += $"   💰 +{card.goldValue} золота\n\n";
        }

        message += $"💎 <b>Осталось гемов:</b> <code>{_userGems[userId]}</code>\n";
        message += $"💰 <b>Всего золота:</b> <code>{(_users.ContainsKey(userId) ? _users[userId].Gold : 0)}</code>";

        // Создаем клавиатуру
        var keyboard = new InlineKeyboardMarkup(new[]
        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 Открыть ещё", $"buy_{chestType}"),
            InlineKeyboardButton.WithCallbackData("🏪 В магазин", "refresh_shop")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📊 Лимит", "chest_limit"),
            InlineKeyboardButton.WithCallbackData("💰 Баланс", "show_balance")
        }
    });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }


    // ============================
    // КОМАНДА КОНВЕРТАЦИИ ЗОЛОТА (/convert)
    // ============================

    private static async Task HandleConvertCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Использование: /convert <количество_золота>\n\n" +
                      "Пример: /convert 100 - обменять 100 золота на 10 гемов\n" +
                      "Курс: 10 золота = 1 гем\n\n" +
                      "Используйте команду /shop для просмотра магазина",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (!int.TryParse(parts[1], out var goldAmount) || goldAmount <= 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверное количество золота. Введите положительное число",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверка минимальной суммы
        if (goldAmount < 10)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Минимальная сумма для обмена: 10 золота",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверяем, кратно ли 10
        if (goldAmount % 10 != 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Количество золота должно быть кратно 10\n" +
                      "Пример: 10, 20, 50, 100, 500",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверяем баланс золота
        if (!_users.ContainsKey(userId) || _users[userId].Gold < goldAmount)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Недостаточно золота!\n\n" +
                      $"💰 Хотите обменять: <code>{goldAmount}</code> золота\n" +
                      $"💰 Ваше золото: <code>{(_users.ContainsKey(userId) ? _users[userId].Gold : 0)}</code>\n\n" +
                      $"Используйте команду /card чтобы заработать больше золота",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Выполняем конвертацию
        var gemsAmount = goldAmount / 10;

        // Снимаем золото
        var user = _users[userId];
        user.Gold -= goldAmount;
        _users[userId] = user;
        SaveUsers();

        // Добавляем гемы
        if (!_userGems.ContainsKey(userId))
        {
            _userGems[userId] = 0;
        }
        _userGems[userId] += gemsAmount;
        SaveUserGems();

        var message = $"✅ <b>Конвертация успешна!</b>\n\n" +
                     $"💰 Списано золота: <code>{goldAmount}</code>\n" +
                     $"💎 Получено гемов: <code>{gemsAmount}</code>\n" +
                     $"📊 Курс: 10 золота = 1 гем\n\n" +
                     $"💰 <b>Текущий баланс золота:</b> <code>{user.Gold}</code>\n" +
                     $"💎 <b>Текущий баланс гемов:</b> <code>{_userGems[userId]}</code>\n\n" +
                     $"<i>Используйте команду /shop для покупки ящиков</i>";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🏪 В магазин", "refresh_shop"),
                InlineKeyboardButton.WithCallbackData("💰 Баланс", "show_balance")
            }
        });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );

        LogInfo($"Пользователь {userId} конвертировал {goldAmount} золота в {gemsAmount} гемов");
    }

    // ============================
    // КОМАНДА БАЛАНСА ГЕМОВ (/gems)
    // ============================

    private static async Task SendUserGemsBalance(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }


    var userGold = _users.ContainsKey(userId) ? _users[userId].Gold : 0;
        var userGems = _userGems.ContainsKey(userId) ? _userGems[userId] : 0;

        var message = $"<b>💰 БАЛАНС</b>\n\n" +
                     $"💰 <b>Золото:</b> <code>{userGold}</code>\n" +
                     $"💎 <b>Гемы:</b> <code>{userGems}</code>\n\n" +
                     $"📊 <b>Курс обмена:</b> 10 золота = 1 гем\n\n" +
                     $"<b>⚡ КОМАНДЫ:</b>\n" +
                     $"<code>/shop</code> - открыть магазин\n" +
                     $"<code>/convert 100</code> - обменять 100 золота\n" +
                     $"<code>/buy wooden</code> - купить ящик";

        var keyboard = new InlineKeyboardMarkup(new[]
     {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🏪 В магазин", "refresh_shop"),
            InlineKeyboardButton.WithCallbackData("💎 Обменять", "convert_gold")
        }
    });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId, // Добавлен
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    // ============================
    // ВСПОМОГАТЕЛЬНЫЙ МЕТОД GetRandomCard
    // ============================

    private static (string fileName, string fullPath) GetRandomCard(string rarity)
    {
        if (!_imageFilesCache.ContainsKey(rarity))
            throw new InvalidOperationException($"Редкость {rarity} не найдена");

        var files = _imageFilesCache[rarity];
        if (files.Length == 0)
            throw new InvalidOperationException($"Нет карт с редкостью {rarity}");

        var randomFile = files[_random.Next(files.Length)];
        var fileName = Path.GetFileName(randomFile);

        return (fileName, randomFile);
    }

    // ============================
    // ВСПОМОГАТЕЛЬНЫЙ МЕТОД GetRandomRarityForChest
    // ============================

    private static string GetRandomRarityForChest(string chestType)
    {
        if (!_chestRarityChances.ContainsKey(chestType))
            return "common";

        var chances = _chestRarityChances[chestType];
        var roll = _random.NextDouble() * 100.0;

        double current = 0;

        foreach (var rarity in chances.Keys)
        {
            current += chances[rarity];
            if (roll < current)
                return rarity;
        }

        return "common";
    }
    private static void LoadUserChestStats()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_userChestStatsFile))
                {
                    var json = File.ReadAllText(_userChestStatsFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, UserChestStats>>(json)
                                 ?? new Dictionary<long, UserChestStats>();

                    _userChestStats = new ConcurrentDictionary<long, UserChestStats>(tempDict);
                }
                else
                {
                    _userChestStats = new ConcurrentDictionary<long, UserChestStats>();
                }
            }
            LogInfo($"Загружено {_userChestStats.Count} записей статистики сундуков");
        }
        catch (Exception ex)
        {
            LogError($"Ошибка загрузки usercheststats.json: {ex.Message}");
            _userChestStats = new ConcurrentDictionary<long, UserChestStats>();
        }
    }

    private static void SaveUserChestStats()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _userChestStats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_userChestStatsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения usercheststats.json: {ex.Message}");
        }
    }

    // Проверяет, может ли пользователь открыть еще сундук
    private static bool CanOpenChest(long userId)
    {
        if (!_userChestStats.ContainsKey(userId))
        {
            _userChestStats[userId] = new UserChestStats { UserId = userId };
        }

        var stats = _userChestStats[userId];

        // Проверяем, не пора ли сбросить лимит (прошел час)
        if (DateTime.UtcNow - stats.ResetTime >= TimeSpan.FromHours(1))
        {
            stats.ChestOpens.Clear();
            stats.ResetTime = DateTime.UtcNow;
            _userChestStats[userId] = stats;
            SaveUserChestStats();
            return true;
        }

        // Удаляем старые записи (старше 1 часа)
        stats.ChestOpens = stats.ChestOpens
            .Where(t => DateTime.UtcNow - t < TimeSpan.FromHours(1))
            .ToList();

        // Проверяем лимит
        return stats.ChestOpens.Count < CHEST_LIMIT_PER_HOUR;
    }

    // Регистрирует открытие сундука
    private static void RegisterChestOpen(long userId, string chestType)
    {
        if (!_userChestStats.ContainsKey(userId))
        {
            _userChestStats[userId] = new UserChestStats { UserId = userId };
        }

        var stats = _userChestStats[userId];

        // Проверяем, не пора ли сбросить лимит
        if (DateTime.UtcNow - stats.ResetTime >= TimeSpan.FromHours(1))
        {
            stats.ChestOpens.Clear();
            stats.ChestCounts["wooden"] = 0;
            stats.ChestCounts["iron"] = 0;
            stats.ChestCounts["golden"] = 0;
            stats.ResetTime = DateTime.UtcNow;
        }

        // Добавляем запись об открытии
        stats.ChestOpens.Add(DateTime.UtcNow);

        // Увеличиваем счетчик для типа сундука
        if (stats.ChestCounts.ContainsKey(chestType))
        {
            stats.ChestCounts[chestType]++;
        }

        _userChestStats[userId] = stats;
        SaveUserChestStats();
    }

    // Получает информацию о лимите для пользователя
    private static (int openedCount, int limit, string timeLeft) GetChestLimitInfo(long userId)
    {
        // Если нет статистики, возвращаем нулевые значения
        if (!_userChestStats.ContainsKey(userId))
        {
            return (0, CHEST_LIMIT_PER_HOUR, "0 минут");
        }

        var stats = _userChestStats[userId];

        // Проверяем, не пора ли сбросить лимит (прошел час с ResetTime)
        var timeSinceReset = DateTime.UtcNow - stats.ResetTime;
        if (timeSinceReset >= TimeSpan.FromHours(1))
        {
            return (0, CHEST_LIMIT_PER_HOUR, "0 минут");
        }

        // Удаляем старые записи (старше 1 часа)
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var validOpens = stats.ChestOpens
            .Where(t => t >= oneHourAgo)
            .ToList();

        var openedCount = validOpens.Count;
        var timeUntilReset = TimeSpan.FromHours(1) - timeSinceReset;

        string timeLeft;
        if (timeUntilReset.TotalMinutes < 1)
        {
            timeLeft = "менее 1 минуты";
        }
        else
        {
            var minutes = (int)Math.Ceiling(timeUntilReset.TotalMinutes);
            timeLeft = $"{minutes} минут";
        }

        return (openedCount, CHEST_LIMIT_PER_HOUR, timeLeft);
    }

    // Получает статистику по сундукам для пользователя
    private static string GetChestStatsForUser(long userId)
    {
        if (!_userChestStats.ContainsKey(userId))
        {
            return "📦 <b>Открыто сундуков:</b> 0\n" +
                   "⏰ <b>Лимит:</b> 3 сундука в час\n" +
                   "🔄 <b>Статистика сбросится через:</b> 0 минут\n" +
                   "💎 <b>Доступно сундуков:</b> 5";
        }

        var stats = _userChestStats[userId];
        var (openedCount, limit, timeLeft) = GetChestLimitInfo(userId);
        var remainingChests = limit - openedCount;
        var totalOpened = stats.ChestCounts.Values.Sum();

        var message = $"📦 <b>Открыто сундуков:</b> {openedCount}/{limit}\n";
        message += $"⏰ <b>Лимит:</b> {limit} сундуков в час\n";
        message += $"🔄 <b>Статистика сбросится через:</b> {timeLeft}\n";
        message += $"💎 <b>Доступно сундуков:</b> {remainingChests}\n\n";

        if (totalOpened > 0)
        {
            message += $"<b>📊 Статистика по типам:</b>\n";
            message += $"📦 Деревянных: {stats.ChestCounts["wooden"]}\n";
            message += $"⚙️ Железных: {stats.ChestCounts["iron"]}\n";
            message += $"💰 Золотых: {stats.ChestCounts["golden"]}\n";
            message += $"📈 Всего открыто: {totalOpened}\n";
        }

        return message;
    }

    private static async Task SendChestLimitInfo(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

    var userGold = _users.ContainsKey(userId) ? _users[userId].Gold : 0;
        var userGems = _userGems.ContainsKey(userId) ? _userGems[userId] : 0;
        var chestStats = GetChestStatsForUser(userId);

        var message = $"<b>📦 ИНФОРМАЦИЯ О ЛИМИТЕ СУНДУКОВ</b>\n\n" +
                     $"{chestStats}\n\n" +
                     $"💰 <b>Ваше золото:</b> <code>{userGold}</code>\n" +
                     $"💎 <b>Ваши гемы:</b> <code>{userGems}</code>\n\n" +
                     $"<b>📋 ПРАВИЛА ЛИМИТА:</b>\n" +
                     $"• Максимум <b>{CHEST_LIMIT_PER_HOUR}</b> сундуков в час\n" +
                     $"• Лимит сбрасывается каждый час\n" +
                     $"• Все типы сундуков учитываются\n" +
                     $"• Можно открыть несколько сундуков подряд\n\n" +
                     $"<b>⚡ КОМАНДЫ:</b>\n" +
                     $"<code>/shop</code> - открыть магазин\n" +
                     $"<code>/buy wooden</code> - купить сундук\n" +
                     $"<code>/convert 100</code> - обменять золото\n" +
                     $"<code>/gems</code> - баланс гемов";

        var keyboard = new InlineKeyboardMarkup(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🏪 В магазин", "refresh_shop"),
            InlineKeyboardButton.WithCallbackData("💎 Баланс", "show_balance")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔄 Проверить лимит", "chest_limit")
        }
    });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId, // Добавлен
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    // ============================
    // ВСПОМОГАТЕЛЬНЫЙ МЕТОД: Поиск карты в коллекции по названию
    // ============================

    private static (string cardId, string cardName, string rarity)? FindCardInCollection(long userId, string cardNameSearch)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
            return null;

        var userCards = _userCards[userId].ToList();
        var matchingCards = new List<(string cardId, string cardName, string rarity)>();

        foreach (var cardId in userCards)
        {
            var cardName = GetCardName(cardId);
            if (cardName.Contains(cardNameSearch, StringComparison.OrdinalIgnoreCase))
            {
                var rarityInfo = GetCardRarityInfo(cardId);
                matchingCards.Add((cardId, cardName, rarityInfo.name));
            }
        }

        if (matchingCards.Count == 0)
            return null;

        // Если найдено несколько карт, возвращаем первую
        // (В UI будем показывать все варианты)
        return matchingCards[0];
    }

    private static List<(string cardId, string cardName, string rarity)> FindAllCardsInCollection(long userId, string cardNameSearch)
    {
        var result = new List<(string cardId, string cardName, string rarity)>();

        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
            return result;

        foreach (var cardId in _userCards[userId])
        {
            var cardName = GetCardName(cardId);
            if (cardName.Contains(cardNameSearch, StringComparison.OrdinalIgnoreCase))
            {
                var rarityInfo = GetCardRarityInfo(cardId);
                result.Add((cardId, cardName, rarityInfo.name));
            }
        }

        return result;
    }

    // ============================
    // ОБНОВЛЕННЫЙ МЕТОД: Установка любимой карты из коллекции
    // ============================

    private static async Task SetFavoriteCardFromCollection(ITelegramBotClient bot, long chatId, long userId, string cardNameSearch, int? messageThreadId, CancellationToken ct)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции! Сначала откройте карты командой /card",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Поиск карты в коллекции
        var matchingCards = FindAllCardsInCollection(userId, cardNameSearch);

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карт с названием '{cardNameSearch}' не найдено в вашей коллекции.\n" +
                      "Используйте команду /deck list чтобы увидеть все свои карты.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (matchingCards.Count > 1)
        {
            var message = $"🔍 Найдено несколько карт по запросу '{cardNameSearch}':\n\n";
            for (int i = 0; i < matchingCards.Count && i < 10; i++)
            {
                var card = matchingCards[i];
                message += $"{i + 1}. {GetRarityEmoji(card.rarity)} <b>{card.cardName}</b> ({card.rarity})\n";
            }

            if (matchingCards.Count > 10)
            {
                message += $"\n... и еще {matchingCards.Count - 10} карт\n";
            }

            message += "\n<b>Уточните название карты:</b>\n";
            message += $"/deck favorite set точное_название\n\n";
            message += "<b>Или используйте номер из списка:</b>\n";
            message += $"/deck favorite set {cardNameSearch} 1 - выбрать первую карту\n";

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Найдена одна карта
        var foundCard = matchingCards[0];

        // Устанавливаем любимую карту
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];
        deck.FavoriteCardId = foundCard.cardId;
        SaveUserDecks();

        var rarityEmoji = GetRarityEmoji(foundCard.rarity);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"❤️ Карта установлена как любимая!\n\n" +
                  $"{rarityEmoji} <b>{foundCard.cardName}</b> ({foundCard.rarity})\n\n" +
                  $"💡 <i>Теперь эта карта отображается как ваша любимая в профиле</i>\n\n" +
                  $"Чтобы посмотреть любимую карту: /deck favorite",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    // ============================
    // МЕТОД: Установка любимой карты по номеру
    // ============================

    private static async Task SetFavoriteCardByNumber(ITelegramBotClient bot, long chatId, long userId, string cardNameSearch, int cardNumber, int? messageThreadId, CancellationToken ct)
    {
        if (!_userCards.ContainsKey(userId) || _userCards[userId].Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ У вас нет карт в коллекции!",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Находим все подходящие карты
        var matchingCards = FindAllCardsInCollection(userId, cardNameSearch);

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карт с названием '{cardNameSearch}' не найдено.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Проверяем номер
        if (cardNumber < 1 || cardNumber > matchingCards.Count)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Неверный номер. Найдено карт: {matchingCards.Count}\n" +
                      $"Используйте число от 1 до {matchingCards.Count}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var selectedCard = matchingCards[cardNumber - 1];

        // Устанавливаем любимую карту
        if (!_userDecks.ContainsKey(userId))
        {
            _userDecks[userId] = new UserDeck
            {
                UserId = userId,
                CardIds = new List<string>(),
                FavoriteCardId = ""
            };
        }

        var deck = _userDecks[userId];
        deck.FavoriteCardId = selectedCard.cardId;
        SaveUserDecks();

        var rarityEmoji = GetRarityEmoji(selectedCard.rarity);

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: $"❤️ Карта установлена как любимая!\n\n" +
                  $"{rarityEmoji} <b>{selectedCard.cardName}</b> ({selectedCard.rarity})\n" +
                  $"📊 Номер в списке: #{cardNumber}\n\n" +
                  $"Чтобы посмотреть любимую карту: /deck favorite",
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleCardsCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Если нет параметров, показываем меню выбора редкости
        if (parts.Length < 2)
        {
            await ShowCardsRarityMenu(bot, chatId, userId, messageThreadId, ct);
            return;
        }

        var rarity = parts[1].ToLower();

        // Проверяем валидность редкости
        if (!_rarities.ContainsKey(rarity))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Неверная редкость. Доступные редкости:\n\n" +
                      "• common (🔵 Обычные)\n" +
                      "• rare (🟠 Редкие)\n" +
                      "• epic (🟣 Эпические)\n" +
                      "• legendary (⚪ Легендарные)\n" +
                      "• champion (🌟 Чемпионские)\n" +
                      "• exclusive (💜 Эксклюзивные)\n\n" +  // Добавьте если есть
                      "Использование: /cards <редкость>\n" +
                      "Пример: /cards epic",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        // Если есть номер карты, показываем конкретную карту
        if (parts.Length >= 3 && int.TryParse(parts[2], out int cardNumber))
        {
            await ShowSpecificCardByRarityAndNumber(bot, chatId, userId, rarity, cardNumber, messageThreadId, ct);
            return;
        }

        // Показываем все карты указанной редкости
        await ShowCardsByRarity(bot, chatId, userId, rarity, messageThreadId, ct);
    }

    private static async Task ShowCardsRarityMenu(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var message = "🎴 <b>ПРОСМОТР КАРТ ПО РЕДКОСТЯМ</b>\n\n" +
                      "Выберите редкость карт для просмотра:\n\n";

        foreach (var rarity in _rarities.Keys)
        {
            if (_imageFilesCache.ContainsKey(rarity))
            {
                var count = _imageFilesCache[rarity].Length;
                var rarityInfo = _rarities[rarity];

                // Считаем карты пользователя в этой редкости
                var userCardsCount = 0;
                if (_userCards.ContainsKey(userId))
                {
                    userCardsCount = _userCards[userId].Count(cardId => cardId.StartsWith(rarity + ":"));
                }

                message += $"{rarityInfo.emoji} <b>{rarityInfo.name}</b> ({rarity}): {userCardsCount}/{count} карт\n";
            }
        }

        // Добавляем exclusive, если его нет в _rarities, но есть в _imageFilesCache
        if (!_rarities.ContainsKey("exclusive") && _imageFilesCache.ContainsKey("exclusive"))
        {
            var count = _imageFilesCache["exclusive"].Length;
            message += $"💜 <b>Эксклюзивная</b> (exclusive): 0/{count} карт\n";
        }

        message += $"\n<b>Использование:</b>\n" +
                   $"/cards &lt;редкость&gt; - показать все карты редкости\n" +  // Исправлено: используем &lt; и &gt; вместо < >
                   $"/cards &lt;редкость&gt; &lt;номер&gt; - показать конкретную карту\n" +  // Исправлено
                   $"/view &lt;название&gt; - найти карту по названию\n\n" +  // Исправлено
                   $"<b>Примеры:</b>\n" +
                   $"<code>/cards epic</code> - показать все эпические карты\n" +
                   $"<code>/cards rare 3</code> - показать 3-ю редкую карту\n" +
                   $"<code>/view дракон</code> - найти карты с 'дракон'";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task ShowCardsByRarity(ITelegramBotClient bot, long chatId, long userId, string rarity, int? messageThreadId, CancellationToken ct)
    {
        if (!_imageFilesCache.ContainsKey(rarity) || _imageFilesCache[rarity].Length == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Нет карт с редкостью {rarity} в базе данных.",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var files = _imageFilesCache[rarity];

        // Получаем информацию о редкости
        string rarityName = rarity;
        string rarityEmoji = "❓";
        int rarityGold = 0;

        if (_rarities.ContainsKey(rarity))
        {
            var rarityInfo = _rarities[rarity];
            rarityGold = rarityInfo.gold;
            rarityEmoji = rarityInfo.emoji;
            rarityName = rarityInfo.name;
        }

        // Получаем карты пользователя
        var userCards = _userCards.ContainsKey(userId) ? _userCards[userId] : new HashSet<string>();

        // Составляем список всех карт в редкости
        var allCards = files
            .Select(filePath =>
            {
                var fileName = Path.GetFileName(filePath);
                var cardId = $"{rarity}:{fileName}";
                var cardName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace('_', ' ')
                    .Replace('-', ' ');
                var hasCard = userCards.Contains(cardId);
                return (cardId, cardName, hasCard);
            })
            .OrderBy(c => c.cardName) // Сортируем по названию
            .ToList();

        var userCardsCount = allCards.Count(c => c.hasCard);
        var percentage = allCards.Count > 0 ? Math.Round((double)userCardsCount / allCards.Count * 100, 1) : 0;

        var message = $"{rarityEmoji} <b>{rarityName} КАРТЫ</b>\n\n";

        // Статистика
        message += $"📊 Всего карт: {allCards.Count}\n";
        message += $"🎴 В вашей коллекции: {userCardsCount}/{allCards.Count}\n";
        message += $"📈 Заполнение: {percentage}%\n";

        if (rarityGold > 0)
        {
            message += $"💰 Стоимость карты: {rarityGold} золота\n\n";
        }
        else
        {
            message += "\n";
        }

        message += "<b>СПИСОК КАРТ:</b>\n\n";

        // Показываем все карты с отметкой о наличии
        for (int i = 0; i < allCards.Count; i++)
        {
            var card = allCards[i];
            var status = card.hasCard ? "✅ " : "❌ ";
            message += $"<b>{i + 1}.</b> {status}<b>{card.cardName}</b>\n";

            // Если сообщение становится слишком длинным, обрезаем
            if (message.Length > 3500 && i < allCards.Count - 1)
            {
                message += $"\n... и ещё {allCards.Count - i - 1} карт";
                break;
            }
        }

        // Примеры команд
        message += $"\n\n<b>📝 КОМАНДЫ:</b>\n";
        if (allCards.Count > 0)
        {
            message += $"<code>/cards {rarity} 1</code> - посмотреть карту #1\n";
            message += $"<code>/view \"{allCards[0].cardName}\"</code> - посмотреть первую карту\n";
        }
        message += $"<code>/cards</code> - меню редкостей\n";
        message += $"<code>/mycards</code> - ваша коллекция";

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task ShowSpecificCardByRarityAndNumber(ITelegramBotClient bot, long chatId, long userId, string rarity, int cardNumber, int? messageThreadId, CancellationToken ct)
    {
        if (!_imageFilesCache.ContainsKey(rarity))
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Неверная редкость: {rarity}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var files = _imageFilesCache[rarity];

        if (cardNumber < 1 || cardNumber > files.Length)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Неверный номер. Всего карт с редкостью {rarity}: {files.Length}\n" +
                      $"Используйте число от 1 до {files.Length}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var fileIndex = cardNumber - 1;
        var filePath = files[fileIndex];
        var fileName = Path.GetFileName(filePath);
        var cardName = Path.GetFileNameWithoutExtension(fileName)
            .Replace('_', ' ')
            .Replace('-', ' ');

        var cardId = $"{rarity}:{fileName}";
        var rarityInfo = _rarities[rarity];

        // Проверяем, есть ли карта у пользователя
        var hasCard = _userCards.ContainsKey(userId) && _userCards[userId].Contains(cardId);

        try
        {
            using var stream = File.OpenRead(filePath);

            var caption = $"{rarityInfo.emoji} <b>{cardName}</b>\n\n" +
                         $"📊 <b>Редкость:</b> {rarityInfo.name}\n" +
                         $"💰 <b>Стоимость:</b> {rarityInfo.gold} золота\n" +
                         $"📈 <b>Вес:</b> {rarityInfo.weight}\n" +
                         $"🎴 <b>Номер:</b> {cardNumber}/{files.Length}\n" +
                         $"📋 <b>В коллекции:</b> {(hasCard ? "✅ Да" : "❌ Нет")}\n\n" +
                         $"<b>ID карты:</b> <code>{cardId}</code>\n\n" +
                         $"💡 <i>Используйте эту карту в командах:</i>\n" +
                         $"<code>/deck add \"{cardName}\"</code> - добавить в колоду\n" +
                         $"<code>/deck favorite set \"{cardName}\"</code> - сделать любимой";

            await bot.SendPhoto(
                chatId: chatId,
                messageThreadId: messageThreadId,
                photo: new InputFileStream(stream, fileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка при загрузке карты: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleViewCardCommand(ITelegramBotClient bot, long chatId, long userId, string[] parts, int? messageThreadId, CancellationToken ct)
    {
        // ИЗМЕНЕНО: Проверяем только подписку на канал
        var isChannelMember = await CheckMemberships(userId, ct);

        if (!isChannelMember)
        {
            string missingText = MessageManager.Get("check_membership_channel");

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: MessageManager.Get("access_denied", missingText),
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "🔍 <b>ПОИСК КАРТЫ</b>\n\n" +
                      "Используйте команду для поиска карты по названию:\n" +
                      "<code>/view название_карты</code>\n\n" +
                      "<b>Примеры:</b>\n" +
                      "<code>/view дракон</code> - найти карты с 'дракон'\n" +
                      "<code>/view огненный</code> - найти карты с 'огненный'\n" +
                      "<code>/view ледяной маг</code> - точное название\n\n" +
                      "💡 <i>Можно искать как по полному названию, так и по части</i>",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var searchTerm = string.Join(" ", parts.Skip(1));
        await SearchAndShowCard(bot, chatId, userId, searchTerm, messageThreadId, ct);
    }

    private static async Task SearchAndShowCard(ITelegramBotClient bot, long chatId, long userId, string searchTerm, int? messageThreadId, CancellationToken ct)
    {
        var matchingCards = new List<(string rarity, string filePath, string fileName, string cardName, int index)>();

        // Ищем карты во всех редкостях
        foreach (var rarity in _imageFilesCache.Keys)
        {
            if (!_imageFilesCache.ContainsKey(rarity) || _imageFilesCache[rarity].Length == 0)
                continue;

            var files = _imageFilesCache[rarity];
            for (int i = 0; i < files.Length; i++)
            {
                var filePath = files[i];
                var fileName = Path.GetFileName(filePath);
                var cardName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace('_', ' ')
                    .Replace('-', ' ');

                if (cardName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    matchingCards.Add((rarity, filePath, fileName, cardName, i));
                }
            }
        }

        if (matchingCards.Count == 0)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"🔍 Карт по запросу '<b>{System.Security.SecurityElement.Escape(searchTerm)}</b>' не найдено.\n\n" +
                      "Попробуйте:\n" +
                      "• Уточнить название\n" +
                      "• Использовать часть названия\n" +
                      "• Проверить правильность написания\n\n" +
                      "Или используйте: <code>/cards</code> для просмотра всех карт",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        if (matchingCards.Count > 1)
        {
            var message = $"🔍 Найдено карт по запросу '<b>{System.Security.SecurityElement.Escape(searchTerm)}</b>': {matchingCards.Count}\n\n";

            // Показываем первые 10 результатов
            for (int i = 0; i < matchingCards.Count && i < 10; i++)
            {
                var card = matchingCards[i];

                // Определяем эмодзи в зависимости от редкости
                string rarityEmoji = "❓";
                string rarityName = card.rarity;

                if (card.rarity == "evolution")
                {
                    rarityEmoji = "🔮";
                    rarityName = "Эволюция";
                }
                else if (_rarities.ContainsKey(card.rarity))
                {
                    var rarityInfo = _rarities[card.rarity];
                    rarityEmoji = rarityInfo.emoji;
                    rarityName = rarityInfo.name;
                }

                // Экранируем название карты для HTML
                string escapedCardName = System.Security.SecurityElement.Escape(card.cardName);
                message += $"{i + 1}. {rarityEmoji} <b>{escapedCardName}</b> ({System.Security.SecurityElement.Escape(rarityName)})\n";
            }

            if (matchingCards.Count > 10)
            {
                message += $"\n... и ещё {matchingCards.Count - 10} карт\n";
            }

            message += "\n<b>📝 Используйте:</b>\n";
            message += $"<code>/view точное_название</code> - для конкретной карты\n";
            message += $"<code>/cards редкость номер</code> - например: /cards epic 1\n";
            message += $"Или уточните поисковый запрос";

            // Создаем клавиатуру с кнопками для быстрого просмотра
            var keyboardButtons = new List<InlineKeyboardButton[]>();

            if (matchingCards.Count > 0)
            {
                for (int i = 0; i < Math.Min(3, matchingCards.Count); i++)
                {
                    var card = matchingCards[i];
                    var buttonText = $"{i + 1}. {card.cardName.Substring(0, Math.Min(10, card.cardName.Length))}...";
                    keyboardButtons.Add(new[]
                    {
                    InlineKeyboardButton.WithCallbackData(buttonText, $"view_{card.rarity}_{card.index}")
                });
                }
            }

            keyboardButtons.Add(new[]
            {
            InlineKeyboardButton.WithCallbackData("📋 Меню карт", "cards_menu"),
            InlineKeyboardButton.WithCallbackData("🎴 Мои карты", "mycards_menu")
        });

            var keyboard = new InlineKeyboardMarkup(keyboardButtons);

            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
            return;
        }

        // Если найдена только одна карта, показываем её
        var foundCard = matchingCards[0];

        // Проверяем, что редкость существует в _rarities
        if (!_rarities.ContainsKey(foundCard.rarity) && foundCard.rarity != "evolution")
        {
            // Если это неизвестная редкость, показываем без информации о редкости
            await ShowUnknownCardDetails(bot, chatId, userId, foundCard, messageThreadId, ct);
        }
        else
        {
            await ShowCardDetails(bot, chatId, userId, foundCard.rarity, foundCard.index, foundCard.cardName, messageThreadId, ct);
        }
    }

    // Добавьте этот метод для показа карт с неизвестной редкостью
    private static async Task ShowUnknownCardDetails(ITelegramBotClient bot, long chatId, long userId,
        (string rarity, string filePath, string fileName, string cardName, int index) card,
        int? messageThreadId, CancellationToken ct)
    {
        try
        {
            using var stream = File.OpenRead(card.filePath);

            var caption = $"❓ <b>{card.cardName}</b>\n\n" +
                         $"📊 <b>Редкость:</b> {card.rarity}\n" +
                         $"🎴 <b>Номер:</b> {card.index + 1}\n\n" +
                         $"<b>ID карты:</b> <code>{card.rarity}:{card.fileName}</code>\n\n" +
                         $"💡 <i>Информация о редкости не найдена в базе данных</i>";

            await bot.SendPhoto(
                chatId: chatId,
                messageThreadId: messageThreadId,
                photo: new InputFileStream(stream, card.fileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка при загрузке карты: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task ShowCardDetails(ITelegramBotClient bot, long chatId, long userId, string rarity, int cardIndex, string cardName, int? messageThreadId, CancellationToken ct)
    {
        var files = _imageFilesCache[rarity];

        if (cardIndex < 0 || cardIndex >= files.Length)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Карта не найдена: {cardName}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
            return;
        }

        var filePath = files[cardIndex];
        var fileName = Path.GetFileName(filePath);
        var cardId = $"{rarity}:{fileName}";

        // Определяем информацию о редкости
        string rarityEmoji = "❓";
        string rarityName = rarity;
        int rarityGold = 0;
        int rarityWeight = 0;

        // Проверяем, есть ли такая редкость в словаре
        if (_rarities.ContainsKey(rarity))
        {
            var rarityInfo = _rarities[rarity];
            rarityEmoji = rarityInfo.emoji;
            rarityName = rarityInfo.name;
            rarityGold = rarityInfo.gold;
            rarityWeight = rarityInfo.weight;
        }
        else if (rarity == "evolution")
        {
            // Эволюции - используем эмодзи 🔮
            rarityEmoji = "🔮"; // Устанавливаем эмодзи здесь
            rarityName = "Эволюция";

            // Пытаемся найти базовую карту, чтобы показать её стоимость
            string baseCardName = cardName.Replace(" (эво)", "").Replace(" (эволюция)", "");

            // Ищем базовую карту в других редкостях
            foreach (var r in _imageFilesCache.Keys)
            {
                if (r == "evolution") continue;

                foreach (var f in _imageFilesCache[r])
                {
                    var baseName = Path.GetFileNameWithoutExtension(f)
                        .Replace('_', ' ')
                        .Replace('-', ' ');

                    if (baseName.Equals(baseCardName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rarities.ContainsKey(r))
                        {
                            rarityGold = _rarities[r].gold * 2; // Удвоенная стоимость базовой карты
                            rarityWeight = _rarities[r].weight * 2;
                        }
                        break;
                    }
                }
            }
        }

        // Проверяем, есть ли карта у пользователя
        var hasCard = _userCards.ContainsKey(userId) && _userCards[userId].Contains(cardId);

        // Проверяем, в колоде ли карта
        var inDeck = false;
        var deckPosition = 0;
        if (_userDecks.ContainsKey(userId))
        {
            var deck = _userDecks[userId];
            deckPosition = deck.CardIds.IndexOf(cardId) + 1;
            inDeck = deckPosition > 0;
        }

        // Проверяем, любимая ли это карта
        var isFavorite = _userDecks.ContainsKey(userId) && _userDecks[userId].FavoriteCardId == cardId;

        try
        {
            using var stream = File.OpenRead(filePath);

            var caption = $"{rarityEmoji} <b>{cardName}</b>\n\n" +
                         $"📊 <b>Редкость:</b> {rarityName}\n";

            if (rarityGold > 0)
            {
                caption += $"💰 <b>Стоимость:</b> {rarityGold} золота {(rarity == "evolution" ? "(удвоенная)" : "")}\n";
            }

            caption += $"📈 <b>Вес:</b> {rarityWeight}\n" +
                       $"🎴 <b>Номер в категории:</b> {cardIndex + 1}/{files.Length}\n\n" +
                       $"<b>💎 СТАТУС:</b>\n" +
                       $"• В коллекции: {(hasCard ? "✅ Да" : "❌ Нет")}\n" +
                       $"• В колоде: {(inDeck ? $"✅ Да (позиция #{deckPosition})" : "❌ Нет")}\n" +
                       $"• Любимая карта: {(isFavorite ? "❤️ Да" : "❌ Нет")}\n\n" +
                       $"<b>ID карты:</b> <code>{cardId}</code>\n\n";

            // Добавляем специальную информацию для эволюций
            if (rarity == "evolution")
            {
                caption += $"🔮 <b>Это эволюционировавшая карта!</b>\n";
                caption += $"💡 Получена путем сбора 6 одинаковых карт\n";
                caption += $"✨ Даёт в 2 раза больше золота при дубликатах!\n\n";
            }

            caption += $"<b>⚡ КОМАНДЫ:</b>\n";

            if (hasCard && !inDeck)
            {
                caption += $"<code>/deck add \"{cardName}\"</code> - добавить в колоду\n";
            }

            if (hasCard && !isFavorite)
            {
                caption += $"<code>/deck favorite set \"{cardName}\"</code> - сделать любимой\n";
            }

            if (inDeck)
            {
                caption += $"<code>/deck remove \"{cardName}\"</code> - удалить из колоды\n";
            }

            caption += $"\n💡 <i>ID карты можно использовать в админ-командах</i>";

            await bot.SendPhoto(
                chatId: chatId,
                messageThreadId: messageThreadId,
                photo: new InputFileStream(stream, fileName),
                caption: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: $"❌ Ошибка при загрузке карты: {ex.Message}",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task HandleCardsCallbackQuery(ITelegramBotClient bot, long chatId, long userId, string data, long messageId, int? messageThreadId, CancellationToken ct)
    {
        var parts = data.Split('_');
        if (parts.Length < 2)
            return;

        var action = parts[1];

        switch (action)
        {
            case "menu":
                await ShowCardsRarityMenu(bot, chatId, userId, messageThreadId, ct);
                break;

            case "common":
            case "rare":
            case "epic":
            case "legendary":
            case "champion":
                await ShowCardsByRarity(bot, chatId, userId, action, messageThreadId, ct);
                break;

            case "all":
                await ShowAllRaritiesSummary(bot, chatId, userId, messageThreadId, ct);
                break;

            default:
                break;
        }

        try
        {
            await bot.DeleteMessage(chatId, (int)messageId, ct);
        }
        catch { }
    }

    private static async Task HandleViewCardCallbackQuery(ITelegramBotClient bot, long chatId, long userId, string data, long messageId, int? messageThreadId, CancellationToken ct)
    {
        var parts = data.Split('_');
        if (parts.Length < 3)
            return;

        var rarity = parts[1];
        if (!int.TryParse(parts[2], out var index))
            return;

        // Получаем название карты для отображения
        var files = _imageFilesCache[rarity];
        if (index >= 0 && index < files.Length)
        {
            var filePath = files[index];
            var fileName = Path.GetFileName(filePath);
            var cardName = Path.GetFileNameWithoutExtension(fileName)
                .Replace('_', ' ')
                .Replace('-', ' ');

            await ShowCardDetails(bot, chatId, userId, rarity, index, cardName, messageThreadId, ct);
        }

        try
        {
            await bot.DeleteMessage(chatId, (int)messageId, ct);
        }
        catch { }
    }

    private static async Task ShowAllRaritiesSummary(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        var totalCards = 0;
        var message = "📊 <b>ВСЕ КАРТЫ ПО РЕДКОСТЯМ</b>\n\n";

        foreach (var rarity in _rarities.Keys)
        {
            if (_imageFilesCache.ContainsKey(rarity))
            {
                var count = _imageFilesCache[rarity].Length;
                totalCards += count;
                var rarityInfo = _rarities[rarity];
                message += $"{rarityInfo.emoji} <b>{rarityInfo.name}:</b> {count} карт\n";
            }
        }

        message += $"\n📈 <b>Всего карт в игре:</b> {totalCards}\n\n";

        // Проверяем, сколько карт есть у пользователя
        if (_userCards.ContainsKey(userId))
        {
            var userCardCount = _userCards[userId].Count;
            var percentage = totalCards > 0 ? Math.Round((double)userCardCount / totalCards * 100, 1) : 0;
            message += $"🎴 <b>Ваша коллекция:</b> {userCardCount}/{totalCards} ({percentage}%)\n\n";
        }

        message += "<b>📝 КОМАНДЫ:</b>\n" +
                   "/cards <редкость> - карты определенной редкости\n" +
                   "/view <название> - найти карту по названию\n" +
                   "/mycards - ваша коллекция";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔵 Обычные", "cards_common"),
            InlineKeyboardButton.WithCallbackData("🟠 Редкие", "cards_rare")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🟣 Эпические", "cards_epic"),
            InlineKeyboardButton.WithCallbackData("⚪ Легендарные", "cards_legendary")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🌟 Чемпионские", "cards_champion")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🎴 Мои карты", "mycards_menu"),
            InlineKeyboardButton.WithCallbackData("📋 Меню", "cards_menu")
        }
    });

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    public class MessageManager
    {
        private static readonly string _messagesFile = @"C:\C#\CardsTelegramBotPirozki\CardsTelegramBotPirozki\Messages\messages.json";
        private static Dictionary<string, object> _messages; // Изменено на object для поддержки массивов
        private static readonly object _lock = new object();

        public static void LoadMessages()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_messagesFile))
                    {
                        var json = File.ReadAllText(_messagesFile);
                        _messages = JsonConvert.DeserializeObject<Dictionary<string, object>>(json)
                                  ?? new Dictionary<string, object>();
                    }
                    else
                    {
                        _messages = new Dictionary<string, object>();
                        Console.WriteLine("⚠️ Файл messages.json не найден. Создайте его!");
                    }
                }
                Console.WriteLine($"✅ Загружено {_messages.Count} сообщений");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка загрузки messages.json: {ex.Message}");
                _messages = new Dictionary<string, object>();
            }
        }

        public static string Get(string key, params object[] args)
        {
            if (_messages == null)
                LoadMessages();

            if (_messages.TryGetValue(key, out var value))
            {
                string message;

                // Проверяем, является ли значение массивом
                if (value is Newtonsoft.Json.Linq.JArray jArray)
                {
                    // Выбираем случайный элемент из массива
                    var random = new Random();
                    var items = jArray.ToObject<string[]>();
                    message = items[random.Next(items.Length)];
                }
                else
                {
                    message = value.ToString();
                }

                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(message, args);
                    }
                    catch
                    {
                        return message;
                    }
                }
                return message;
            }

            Console.WriteLine($"⚠️ Сообщение с ключом '{key}' не найдено");
            return $"<b>Ошибка:</b> Сообщение '{key}' не найдено";
        }

        // Новый метод для получения массива сообщений
        public static string[] GetArray(string key)
        {
            if (_messages == null)
                LoadMessages();

            if (_messages.TryGetValue(key, out var value))
            {
                if (value is Newtonsoft.Json.Linq.JArray jArray)
                {
                    return jArray.ToObject<string[]>();
                }
            }

            Console.WriteLine($"⚠️ Массив с ключом '{key}' не найден");
            return new[] { $"<b>Ошибка:</b> Массив '{key}' не найден" };
        }
    }

    // ============================
    // ПРОСТАЯ ФУНКЦИЯ АВТООЧИСТКИ КОНСОЛИ
    // ============================

    private static void StartConsoleAutoClearSimple()
    {
        _ = Task.Run(async () =>
        {
            int counter = 0;
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(20));
                    counter++;

                    Console.Clear();

                    Console.WriteLine($"=== БОТ 'ПИРОЖКИ' === (автоочистка #{counter})");
                    Console.WriteLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"Пользователей: {_users?.Count ?? 0}");
                    Console.WriteLine($"Активных сессий: {_userChestStats?.Count ?? 0}");
                    Console.WriteLine($"Загружено карт: {_imageFilesCache?.Sum(x => x.Value.Length) ?? 0}");
                    Console.WriteLine("\n" + new string('-', 50));
                    Console.WriteLine("Новые сообщения:");
                    Console.WriteLine(new string('-', 50) + "\n");
                }
                catch { }
            }
        });
    }

    // ============================
    // ЗАГРУЗКА И СОХРАНЕНИЕ ЭВОЛЮЦИЙ
    // ============================

    private static void LoadEvolutions()
    {
        try
        {
            Console.WriteLine("\n=== ЗАГРУЗКА ЭВОЛЮЦИЙ ===");
            Console.WriteLine($"Путь к файлу: {_evolutionsFile}");

            // Проверяем существование файла
            if (!File.Exists(_evolutionsFile))
            {
                Console.WriteLine($"❌ Файл не найден: {_evolutionsFile}");
                Console.WriteLine("🔄 Создаю пустой файл эволюций...");

                var emptyWrapper = new EvolutionListWrapper
                {
                    Evolutions = new List<EvolutionData>()
                };

                var emptyJson = JsonConvert.SerializeObject(emptyWrapper, Formatting.Indented);
                File.WriteAllText(_evolutionsFile, emptyJson, Encoding.UTF8);

                _evolutions = new ConcurrentDictionary<string, EvolutionData>();
                Console.WriteLine("✅ Создан пустой файл эволюций");
                return;
            }

            // Читаем файл с правильной кодировкой
            string json = File.ReadAllText(_evolutionsFile, Encoding.UTF8);
            Console.WriteLine($"📄 Прочитано {json.Length} символов");

            // Дополнительная проверка на BOM
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
                Console.WriteLine("⚠️ Удален BOM из файла");
            }

            // Парсим JSON
            var evolutionWrapper = JsonConvert.DeserializeObject<EvolutionListWrapper>(json);

            _evolutions = new ConcurrentDictionary<string, EvolutionData>();

            if (evolutionWrapper?.Evolutions != null && evolutionWrapper.Evolutions.Any())
            {
                Console.WriteLine($"✅ Найдено эволюций в JSON: {evolutionWrapper.Evolutions.Count}");

                // Словарь для отслеживания дубликатов
                var uniqueEvolutions = new Dictionary<string, EvolutionData>();
                var duplicateNames = new List<string>();

                foreach (var evo in evolutionWrapper.Evolutions)
                {
                    if (evo == null) continue;

                    // Валидация данных
                    if (string.IsNullOrEmpty(evo.EvolutionName))
                    {
                        Console.WriteLine("⚠️ Пропущена эволюция без названия");
                        continue;
                    }

                    if (evo.RequiredCount <= 0)
                    {
                        Console.WriteLine($"⚠️ Эволюция '{evo.EvolutionName}' имеет некорректное required_count: {evo.RequiredCount}, устанавливаю 6");
                        evo.RequiredCount = 6;
                    }

                    if (evo.RewardMultiplier <= 0)
                    {
                        Console.WriteLine($"⚠️ Эволюция '{evo.EvolutionName}' имеет некорректный reward_multiplier: {evo.RewardMultiplier}, устанавливаю 2.0");
                        evo.RewardMultiplier = 2.0;
                    }

                    // Устанавливаем базовую карту из RequiredCards если нужно
                    if (evo.RequiredCards != null && evo.RequiredCards.Any() && string.IsNullOrEmpty(evo.BaseCard))
                    {
                        evo.BaseCard = evo.RequiredCards.First();
                    }

                    // Проверяем на дубликаты
                    if (uniqueEvolutions.ContainsKey(evo.EvolutionName))
                    {
                        duplicateNames.Add(evo.EvolutionName);
                        Console.WriteLine($"⚠️ Обнаружен дубликат эволюции: {evo.EvolutionName}");
                    }
                    else
                    {
                        uniqueEvolutions[evo.EvolutionName] = evo;
                    }
                }

                // Загружаем уникальные эволюции
                foreach (var evo in uniqueEvolutions)
                {
                    _evolutions[evo.Key] = evo.Value;
                }

                Console.WriteLine($"\n📊 СТАТИСТИКА ЗАГРУЗКИ:");
                Console.WriteLine($"   Уникальных названий: {_evolutions.Count}");
                Console.WriteLine($"   Всего эволюций в JSON: {evolutionWrapper.Evolutions.Count}");

                if (duplicateNames.Any())
                {
                    Console.WriteLine($"   Пропущено дубликатов: {duplicateNames.Count}");
                    Console.WriteLine($"   Дубликаты: {string.Join(", ", duplicateNames.Distinct())}");
                }
            }
            else
            {
                Console.WriteLine("⚠️ В файле нет эволюций или неверный формат");
                _evolutions = new ConcurrentDictionary<string, EvolutionData>();
            }

            Console.WriteLine($"\n✅ ИТОГО загружено эволюций: {_evolutions.Count}");

            // Показываем список загруженных эволюций
            if (_evolutions.Any())
            {
                Console.WriteLine("\n📋 ЗАГРУЖЕННЫЕ ЭВОЛЮЦИИ:");
                int index = 1;
                foreach (var evo in _evolutions.Values.OrderBy(e => e.EvolutionName))
                {
                    Console.WriteLine($"  {index++}. {evo.EvolutionName} (нужно: {evo.RequiredCount} {evo.GetBaseCardName()})");
                }
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"\n❌ ОШИБКА ПАРСИНГА JSON: {ex.Message}");
            Console.WriteLine("🔄 Создаю резервную копию и новый файл...");

            try
            {
                // Создаем резервную копию испорченного файла
                string backupFile = _evolutionsFile + ".backup." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(_evolutionsFile, backupFile);
                Console.WriteLine($"✅ Создана резервная копия: {backupFile}");

                // Создаем новый пустой файл
                var emptyWrapper = new EvolutionListWrapper { Evolutions = new List<EvolutionData>() };
                var emptyJson = JsonConvert.SerializeObject(emptyWrapper, Formatting.Indented);
                File.WriteAllText(_evolutionsFile, emptyJson, Encoding.UTF8);
                Console.WriteLine("✅ Создан новый файл эволюций");
            }
            catch (Exception backupEx)
            {
                Console.WriteLine($"❌ Ошибка при создании резервной копии: {backupEx.Message}");
            }

            _evolutions = new ConcurrentDictionary<string, EvolutionData>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n❌ ОШИБКА ЗАГРУЗКИ: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            _evolutions = new ConcurrentDictionary<string, EvolutionData>();
        }
    }

    // Добавьте этот метод в класс Program

    private static void LoadUserEvolutions()
    {
        try
        {
            lock (_fileLock)
            {
                if (File.Exists(_userEvolutionsFile))
                {
                    var json = File.ReadAllText(_userEvolutionsFile);
                    var tempDict = JsonConvert.DeserializeObject<Dictionary<long, UserEvolutionsData>>(json)
                                 ?? new Dictionary<long, UserEvolutionsData>();
                    _userEvolutions = new ConcurrentDictionary<long, UserEvolutionsData>(tempDict);
                }
                else
                {
                    _userEvolutions = new ConcurrentDictionary<long, UserEvolutionsData>();
                }
            }
            Console.WriteLine($"✅ Загружено {_userEvolutions.Count} записей эволюций пользователей");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка загрузки userevolutions.json: {ex.Message}");
            _userEvolutions = new ConcurrentDictionary<long, UserEvolutionsData>();
        }
    }

    private static void SaveUserEvolutions()
    {
        try
        {
            lock (_fileLock)
            {
                var tempDict = _userEvolutions.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var json = JsonConvert.SerializeObject(tempDict, Formatting.Indented);
                File.WriteAllText(_userEvolutionsFile, json);
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка сохранения userevolutions.json: {ex.Message}");
        }
    }



    // ============================
    // ПРОВЕРКА И АКТИВАЦИЯ ЭВОЛЮЦИЙ
    // ============================

    private static async Task CheckAndActivateEvolutions(ITelegramBotClient bot, long userId, string cardName, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            if (!_userCards.ContainsKey(userId))
                return;

            if (_evolutions == null || _evolutions.IsEmpty)
                return;

            // Инициализируем данные пользователя
            if (!_userEvolutions.ContainsKey(userId))
            {
                _userEvolutions[userId] = new UserEvolutionsData
                {
                    UserId = userId,
                    EvolutionsObtained = new Dictionary<string, bool>(),
                    CardCounts = new Dictionary<string, int>(),
                    EvolutionObtainedTime = new Dictionary<string, DateTime>()
                };
            }

            var userEvo = _userEvolutions[userId];

            // Инициализируем словари если null
            if (userEvo.CardCounts == null)
                userEvo.CardCounts = new Dictionary<string, int>();
            if (userEvo.EvolutionsObtained == null)
                userEvo.EvolutionsObtained = new Dictionary<string, bool>();

            // Обновляем счетчик
            if (!userEvo.CardCounts.ContainsKey(cardName))
                userEvo.CardCounts[cardName] = 0;

            userEvo.CardCounts[cardName]++;

            LogInfo($"Пользователь {userId}: карта '{cardName}' теперь {userEvo.CardCounts[cardName]} шт.");

            // Проверяем эволюции
            bool evolutionActivated = false;

            foreach (var evo in _evolutions.Values)
            {
                string baseCardName = evo.GetBaseCardName();

                if (!string.Equals(baseCardName, cardName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (userEvo.EvolutionsObtained.ContainsKey(evo.EvolutionName) &&
                    userEvo.EvolutionsObtained[evo.EvolutionName])
                    continue;

                int currentCount = userEvo.CardCounts.ContainsKey(cardName) ? userEvo.CardCounts[cardName] : 0;

                if (currentCount >= evo.RequiredCount)
                {
                    await ActivateEvolution(bot, userId, evo, messageThreadId, ct);
                    evolutionActivated = true;
                    break;
                }
            }

            if (evolutionActivated)
            {
                _userEvolutions[userId] = userEvo;
                SaveUserEvolutions();
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка в CheckAndActivateEvolutions: {ex.Message}");
        }
    }

    // Вспомогательный метод для проверки существования карты в игре
    private static bool CardExistsInGame(string cardName)
    {
        foreach (var rarity in _imageFilesCache.Keys)
        {
            foreach (var filePath in _imageFilesCache[rarity])
            {
                var fileName = Path.GetFileName(filePath);
                var currentCardName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace('_', ' ')
                    .Replace('-', ' ');

                if (string.Equals(currentCardName, cardName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static async Task ActivateEvolution(ITelegramBotClient bot, long userId, EvolutionData evo, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            if (!_userEvolutions.ContainsKey(userId))
            {
                _userEvolutions[userId] = new UserEvolutionsData
                {
                    UserId = userId,
                    EvolutionsObtained = new Dictionary<string, bool>(),
                    CardCounts = new Dictionary<string, int>(),
                    EvolutionObtainedTime = new Dictionary<string, DateTime>()
                };
            }

            var userEvo = _userEvolutions[userId];

            // Инициализируем словари если null
            if (userEvo.EvolutionsObtained == null)
                userEvo.EvolutionsObtained = new Dictionary<string, bool>();
            if (userEvo.EvolutionObtainedTime == null)
                userEvo.EvolutionObtainedTime = new Dictionary<string, DateTime>();

            // Проверяем, не активирована ли уже эволюция
            if (userEvo.EvolutionsObtained.ContainsKey(evo.EvolutionName) &&
                userEvo.EvolutionsObtained[evo.EvolutionName])
            {
                LogInfo($"Эволюция {evo.EvolutionName} уже активирована у пользователя {userId}");
                return;
            }

            // Отмечаем эволюцию как полученную
            userEvo.EvolutionsObtained[evo.EvolutionName] = true;
            userEvo.EvolutionObtainedTime[evo.EvolutionName] = DateTime.UtcNow;

            _userEvolutions[userId] = userEvo;
            SaveUserEvolutions();

            LogInfo($"Пользователь {userId} активировал эволюцию {evo.EvolutionName}");

            // Отправляем уведомление
            await SendEvolutionNotification(bot, userId, evo, messageThreadId, ct);
        }
        catch (Exception ex)
        {
            LogError($"Ошибка в ActivateEvolution: {ex.Message}");
        }
    }

    private static async Task SendEvolutionNotification(ITelegramBotClient bot, long userId, EvolutionData evo, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            string baseCardName = evo.GetBaseCardName();
            var rarityInfo = GetCardRarityInfoForEvolution(baseCardName);

            string caption = $"🌟 <b>ЭВОЛЮЦИЯ АКТИВИРОВАНА!</b>\n\n" +
                            $"Вы собрали {evo.RequiredCount} карт {rarityInfo.emoji} <b>{baseCardName}</b> " +
                            $"и получили эволюцию!\n\n" +
                            $"📦 <b>{evo.EvolutionName}</b>\n" +
                            $"🔹 Базовая карта: {baseCardName}\n" +
                            $"🔹 Требовалось карт: {evo.RequiredCount}\n" +
                            $"🔹 Множитель награды: x{evo.RewardMultiplier}\n\n" +
                            $"💡 Используйте команду /myevolutions чтобы увидеть все ваши эволюции.";

            await bot.SendMessage(
                chatId: userId,
                text: caption,
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            LogError($"Ошибка в SendEvolutionNotification: {ex.Message}");
        }
    }

    // Вспомогательный метод для поиска картинки эволюции
    private static string FindEvolutionImage(string folder, string evolutionName, string baseCardName)
    {
        var files = Directory.GetFiles(folder, "*.*")
            .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (files.Length == 0)
            return null;

        // Нормализуем названия для поиска
        string normalizedEvoName = evolutionName
            .Replace(" (эво)", "")
            .Replace(' ', '_')
            .Replace('-', '_')
            .ToLower();

        string normalizedBaseName = baseCardName
            .Replace(' ', '_')
            .Replace('-', '_')
            .ToLower();

        // Сначала ищем по точному названию эволюции
        foreach (var file in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
            if (fileName.Equals(normalizedEvoName, StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(normalizedBaseName + "_evo", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(normalizedBaseName + "_evolution", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        // Затем ищем по содержанию
        foreach (var file in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
            if (fileName.Contains(normalizedEvoName) ||
                (fileName.Contains(normalizedBaseName) && fileName.Contains("evo")))
            {
                return file;
            }
        }

        // Возвращаем null если ничего не нашли
        return null;
    }

    // Вспомогательный метод для получения информации о редкости карты
    private static (string emoji, string name, int weight) GetCardRarityInfoForEvolution(string cardName)
    {
        foreach (var rarity in _imageFilesCache.Keys)
        {
            if (rarity == "evolution") continue; // Пропускаем папку с эволюциями

            foreach (var filePath in _imageFilesCache[rarity])
            {
                var fileName = Path.GetFileName(filePath);
                var currentCardName = Path.GetFileNameWithoutExtension(fileName)
                    .Replace('_', ' ')
                    .Replace('-', ' ');

                if (string.Equals(currentCardName, cardName, StringComparison.OrdinalIgnoreCase))
                {
                    if (_rarities.ContainsKey(rarity))
                    {
                        var (gold, emoji, name, weight) = _rarities[rarity];
                        return (emoji, name, weight);
                    }
                }
            }
        }

        // Если не нашли, возвращаем значения по умолчанию
        return ("🔮", "Эволюция", 0);
    }

    private static bool HasEvolutionForCard(long userId, string cardName)
    {
        try
        {
            if (!_userEvolutions.ContainsKey(userId))
                return false;

            var userEvo = _userEvolutions[userId];

            // Проверяем, есть ли эволюция, которая использует эту карту
            foreach (var evo in _evolutions.Values)
            {
                // Получаем базовую карту для эволюции
                string baseCardName = evo.GetBaseCardName();

                // Проверяем совпадение с текущей картой
                if (!string.Equals(baseCardName, cardName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Проверяем, получена ли эволюция
                if (userEvo.EvolutionsObtained.ContainsKey(evo.EvolutionName) &&
                    userEvo.EvolutionsObtained[evo.EvolutionName])
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            LogError($"Ошибка в HasEvolutionForCard: {ex.Message}");
            return false;
        }
    }

    // ============================
    // ПРОСМОТР ЭВОЛЮЦИЙ
    // ============================

    private static async Task SendUserEvolutions(ITelegramBotClient bot, long chatId, long userId, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            // ИЗМЕНЕНО: Проверяем только подписку на канал
            var isChannelMember = await CheckMemberships(userId, ct);

            if (!isChannelMember)
            {
                string missingText = MessageManager.Get("check_membership_channel");

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: MessageManager.Get("access_denied", missingText),
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            // Проверяем наличие эволюций в игре
            if (_evolutions == null || _evolutions.IsEmpty)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "🌟 <b>ЭВОЛЮЦИИ</b>\n\n" +
                          "В игре пока нет доступных эволюций.\n\n" +
                          "💡 <i>Эволюции появятся позже!</i>",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            // Проверяем наличие эволюций у пользователя
            if (!_userEvolutions.ContainsKey(userId))
            {
                await SendNoEvolutionsMessage(bot, chatId, messageThreadId, ct);
                return;
            }

            var userEvo = _userEvolutions[userId];

            // Получаем список названий эволюций
            var obtainedEvolutionsList = userEvo.EvolutionsObtained
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .OrderByDescending(name => userEvo.EvolutionObtainedTime.ContainsKey(name)
                    ? userEvo.EvolutionObtainedTime[name]
                    : DateTime.MinValue)
                .ToList();

            if (obtainedEvolutionsList.Count == 0)
            {
                await SendNoEvolutionsMessage(bot, chatId, messageThreadId, ct);
                return;
            }

            // Формируем сообщение
            var message = "🌟 <b>ВАШИ ЭВОЛЮЦИИ</b>\n\n";
            message += $"📊 Всего эволюций: {obtainedEvolutionsList.Count}\n\n";

            var keyboardButtons = new List<List<InlineKeyboardButton>>();

            for (int i = 0; i < obtainedEvolutionsList.Count && i < 10; i++)

            {
                string evoName = obtainedEvolutionsList[i];

                if (!_evolutions.ContainsKey(evoName))
                    continue;

                var evoData = _evolutions[evoName];
                var obtainedTime = userEvo.EvolutionObtainedTime.ContainsKey(evoName)
                    ? userEvo.EvolutionObtainedTime[evoName]
                    : DateTime.MinValue;

                string baseCardName = evoData.GetBaseCardName();
                var rarityInfo = GetCardRarityInfoForEvolution(baseCardName);

                message += $"📦 {rarityInfo.emoji} <b>{evoName}</b>\n";
                message += $"   🔹 Базовая карта: {baseCardName}\n";
                message += $"   🔹 Получена: {obtainedTime:dd.MM.yyyy}\n";
                message += $"   🔹 Множитель: x{evoData.RewardMultiplier} золота\n\n";

                // Добавляем кнопку для просмотра деталей
                if (keyboardButtons.Count < 5)
                {
                    // Правильно: создаем List<InlineKeyboardButton> и добавляем его
                    var buttonRow = new List<InlineKeyboardButton>
    {
        InlineKeyboardButton.WithCallbackData($"🔍 {evoName}", $"view_evo_{i}")
    };
                    keyboardButtons.Add(buttonRow);
                }

                if (obtainedEvolutionsList.Count > 10)
                {
                    message += $"... и ещё {obtainedEvolutionsList.Count - 10} эволюций\n\n";
                }

                message += "💡 <i>Эволюционировавшие карты дают в 2 раза больше золота " +
                          "при повторном выпадении базовой карты!</i>";

                InlineKeyboardMarkup keyboard = null;
                if (keyboardButtons.Any())
                {
                    keyboard = new InlineKeyboardMarkup(keyboardButtons);
                }

                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: message,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
            }
        }

        catch (Exception ex)
        {
            LogError($"Ошибка в SendUserEvolutions: {ex.Message}");
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Произошла ошибка при загрузке эволюций",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    private static async Task SendNoEvolutionsMessage(ITelegramBotClient bot, long chatId, int? messageThreadId, CancellationToken ct)
    {
        var message = "🌟 <b>ЭВОЛЮЦИИ</b>\n\n" +
                      "У вас пока нет эволюций.\n\n" +
                      "💡 <b>Как получить эволюцию?</b>\n" +
                      "Соберите 6 одинаковых карт, чтобы получить их эволюцию!\n" +
                      "Эволюционировавшая карта дает в 2 раза больше золота.\n\n" +
                      "📋 <b>Доступные эволюции в игре:</b>\n";

        if (_evolutions != null && !_evolutions.IsEmpty)
        {
            foreach (var evo in _evolutions.Values.Take(5))
            {
                message += $"• {evo.EvolutionName} (нужно {evo.RequiredCount} {evo.GetBaseCardName()})\n";
            }

            if (_evolutions.Count > 5)
            {
                message += $"• ... и ещё {_evolutions.Count - 5} эволюций\n";
            }
        }
        else
        {
            message += "Пока нет доступных эволюций";
        }

        await bot.SendMessage(
            chatId: chatId,
            messageThreadId: messageThreadId,
            text: message,
            parseMode: ParseMode.Html,
            cancellationToken: ct
        );
    }

    private static async Task HandleViewEvolutionCallback(ITelegramBotClient bot, long chatId, long userId, string data, int? messageThreadId, CancellationToken ct)
    {
        try
        {
            var parts = data.Split('_');
            if (parts.Length < 3 || !int.TryParse(parts[2], out int index))
            {
                await bot.AnswerCallbackQuery(callbackQueryId: data, text: "❌ Неверный формат запроса");
                return;
            }

            if (!_userEvolutions.ContainsKey(userId))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ У вас нет эволюций",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var userEvo = _userEvolutions[userId];
            var obtainedEvolutions = userEvo.EvolutionsObtained
                .Where(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            if (index < 0 || index >= obtainedEvolutions.Count)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: "❌ Эволюция не найдена",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            string evoName = obtainedEvolutions[index];

            if (!_evolutions.ContainsKey(evoName))
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: $"❌ Данные об эволюции '{evoName}' не найдены",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct
                );
                return;
            }

            var evoData = _evolutions[evoName];
            string baseCardName = evoData.GetBaseCardName();
            var rarityInfo = GetCardRarityInfoForEvolution(baseCardName);

            // Ищем картинку
            string evolutionImagePath = null;
            string evolutionFolder = _imageFolders["evolution"];

            if (Directory.Exists(evolutionFolder))
            {
                evolutionImagePath = FindEvolutionImage(evolutionFolder, evoName, baseCardName);
            }

            string caption = $"💫 <b>{evoName}</b>\n\n" +
                            $"{rarityInfo.emoji} <b>Базовая карта:</b> {baseCardName}\n" +
                            $"🔹 <b>Требовалось карт:</b> {evoData.RequiredCount}\n" +
                            $"🔹 <b>Множитель:</b> x{evoData.RewardMultiplier}\n";

            if (userEvo.EvolutionObtainedTime.ContainsKey(evoName))
            {
                caption += $"🔹 <b>Получена:</b> {userEvo.EvolutionObtainedTime[evoName]:dd.MM.yyyy HH:mm}\n";
            }

            caption += $"\n💡 <i>При выпадении базовой карты даёт в {evoData.RewardMultiplier} раза больше золота!</i>\n\n";
            caption += $"ID эволюции: <code>{evoName}</code>";

            // Создаем клавиатуру для возврата
            var keyboard = new InlineKeyboardMarkup(new[]
            {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("◀️ Назад к эволюциям", "myevolutions_menu")
            }
        });

            if (evolutionImagePath != null && File.Exists(evolutionImagePath))
            {
                var fileInfo = new FileInfo(evolutionImagePath);
                if (fileInfo.Length <= MAX_FILE_SIZE_MB * 1024 * 1024)
                {
                    using var stream = File.OpenRead(evolutionImagePath);
                    var fileName = Path.GetFileName(evolutionImagePath);

                    await bot.SendPhoto(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        photo: new InputFileStream(stream, fileName),
                        caption: caption,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: ct
                    );
                }
                else
                {
                    // Если файл слишком большой, отправляем текст
                    await bot.SendMessage(
                        chatId: chatId,
                        messageThreadId: messageThreadId,
                        text: caption + "\n\n⚠️ Картинка слишком большая для отправки",
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: ct
                    );
                }
            }
            else
            {
                await bot.SendMessage(
                    chatId: chatId,
                    messageThreadId: messageThreadId,
                    text: caption,
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
            }
        }
        catch (Exception ex)
        {
            LogError($"Ошибка в HandleViewEvolutionCallback: {ex.Message}");
            await bot.SendMessage(
                chatId: chatId,
                messageThreadId: messageThreadId,
                text: "❌ Ошибка при загрузке эволюции",
                parseMode: ParseMode.Html,
                cancellationToken: ct
            );
        }
    }

    // ============================
    // АДМИН-ФУНКЦИЯ: Сброс кэша подписки
    // ============================

    private static void ClearSubscriptionCache(long? userId = null)
    {
        if (userId.HasValue)
        {
            _subscriptionCache.TryRemove(userId.Value, out _);
            LogInfo($"Кэш подписки сброшен для пользователя {userId}");
        }
        else
        {
            _subscriptionCache.Clear();
            LogInfo($"Кэш подписки полностью сброшен");
        }
    }

    private static ConcurrentDictionary<long, (bool isSubscribed, DateTime checkedAt)> _subscriptionCache
    = new ConcurrentDictionary<long, (bool, DateTime)>();

    // Время жизни кэша (например, 1 час)
    private static readonly TimeSpan CACHE_TTL = TimeSpan.FromHours(1);



    // ============================
    // КЛАССЫ КОНФИГУРАЦИИ И ДАННЫХ
    // ============================

    public class BotConfig
    {
        public string BotToken { get; set; } = "";
    }

    public class UserData
    {
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public int Gold { get; set; }
        public int Gems { get; set; }
        public int TotalCards { get; set; }
        public int CommonCards { get; set; }
        public int RareCards { get; set; }
        public int EpicCards { get; set; }
        public int LegendaryCards { get; set; }
        public int ChampionCards { get; set; }
        public int TotalGoldEarned { get; set; }
        public DateTime Registered { get; set; }
        public DateTime LastCardTime { get; set; }
    }

    public class UserDeck
    {
        public long UserId { get; set; }
        public List<string> CardIds { get; set; } = new List<string>();
        public string FavoriteCardId { get; set; } = "";
    }

    public class AdminData
    {
        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string FirstName { get; set; } = "";
        public AdminLevel Level { get; set; } = AdminLevel.Admin;
        public long AddedBy { get; set; }
        public DateTime AddedDate { get; set; }
    }

    public class PromoCodeData
    {
        public string Code { get; set; } = "";
        public int GoldReward { get; set; }
        public string CardReward { get; set; } = ""; // rarity:filename
        public int MaxUses { get; set; }
        public int UsedCount { get; set; }
        public DateTime Created { get; set; }
        public DateTime? Expires { get; set; }
        public long CreatedBy { get; set; }
        public List<long> UsedBy { get; set; } = new List<long>();
    }

    public class UserEvolutionsData
    {
        public long UserId { get; set; }
        public Dictionary<string, bool> EvolutionsObtained { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, int> CardCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, DateTime> EvolutionObtainedTime { get; set; } = new Dictionary<string, DateTime>();
    }

    // ============================
    // КЛАССЫ ДЛЯ ЭВОЛЮЦИЙ
    // ============================    

    public class EvolutionListWrapper
    {
        [JsonProperty("evolutions")]
        public List<EvolutionData> Evolutions { get; set; } = new List<EvolutionData>();
    }


    public class EvolutionData
    {
        [JsonProperty("evolution_name")]
        public string EvolutionName { get; set; } = "";

        [JsonProperty("required_cards")]
        public List<string> RequiredCards { get; set; } = new List<string>();

        [JsonProperty("required_count")]
        public int RequiredCount { get; set; } = 6;

        [JsonProperty("reward_multiplier")]
        public double RewardMultiplier { get; set; } = 2.0;

        // Уберите JsonIgnore, если хотите сохранять BaseCard
        [JsonProperty("base_card")] // Добавьте для сохранения
        public string BaseCard { get; set; } = "";

        public string GetBaseCardName()
        {
            if (!string.IsNullOrEmpty(BaseCard))
                return BaseCard;

            if (RequiredCards != null && RequiredCards.Any())
            {
                BaseCard = RequiredCards.First();
                return BaseCard;
            }

            return "Неизвестно";
        }
    }


    public enum AdminLevel
    {
        Admin = 0,
        SuperAdmin = 1,
        Owner = 2
    }
}
