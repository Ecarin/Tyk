using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Tyk.Application.Interfaces;
using Tyk.Domain.Entities;
using PersianCalendar = System.Globalization.PersianCalendar;

namespace Tyk.Application.Services;

/// <summary>
///     Presence-tracking Telegram bot.
///     .NET 9 / C# 13 – relies on Telegram.Bot 22.5.1
/// </summary>
public sealed class TelegramBotService : IHostedService, IDisposable
{
    private const string REP_YEAR = "rep_y_";
    private const string REP_MONTH = "rep_m_";

    private const bool TEST_MODE = false; // ← set to false for production
    private readonly IConfiguration _configuration;

    private readonly TimeZoneInfo _iranTz =
        TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");

    private readonly ILogger<TelegramBotService> _logger;

    private readonly Dictionary<long /*callback msgId*/, long /*userId*/> _pendingConfirmations = new();
    private readonly IServiceProvider _serviceProvider;

    private ITelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private Timer? _midnightTimer; // once a day

    private Timer? _statusTimer; // every 10 s
    private readonly Dictionary<int, CancellationTokenSource> _confirmTimers = new();


    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim>
        _chatLocks = new();


    public TelegramBotService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<TelegramBotService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();
        _midnightTimer?.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IHostedService
    // ──────────────────────────────────────────────────────────────────────────
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Telegram bot…");

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN env-var is missing.");

        _botClient = new TelegramBotClient(token);
        _cts = new CancellationTokenSource();

        // start long-polling
        _botClient.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>(),
                DropPendingUpdates = true
            },
            _cts.Token);

        // periodic status update – every 10 s
        _statusTimer = new Timer(OnStatusTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        // schedule midnight reset (Iran TZ)
        ScheduleMidnightReset();

        var me = await _botClient.GetMe(cancellationToken);
        _logger.LogInformation("Bot @{Username} started.", me.Username);
        await EnsureDailyStatusBoardAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _statusTimer?.Dispose();
        _midnightTimer?.Dispose();
        _cts?.Cancel();
        _logger.LogInformation("Bot service stopped.");
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Timers
    // ──────────────────────────────────────────────────────────────────────────
    private async void OnStatusTick(object? _)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();
            var chatIds = await repository.GetTrackedChatsAsync();

            foreach (var chatId in chatIds)
                await UpdateStatusMessage(chatId, repository);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic status-update error");
        }
    }

    /// Schedules the “new-day” routine.  
    /// In TEST_MODE it triggers every 1 minute; otherwise at Iran midnight daily.
    private void ScheduleMidnightReset()
    {
        if (TEST_MODE)
        {
            _midnightTimer = new Timer(async _ => await ResetDayEndStatus(),
                null,
                TimeSpan.FromMinutes(1), // first run in 1 min
                TimeSpan.FromMinutes(1)); // repeat every 1 min
            return;
        }

        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);
        var nextMidnight = iranNow.Date.AddDays(1);
        var delay = nextMidnight - iranNow;

        _midnightTimer = new Timer(async _ => await ResetDayEndStatus(),
            null,
            delay, // first run at real midnight
            TimeSpan.FromDays(1)); // then every 24 h
    }


    /// Midnight routine (or every minute in TEST_MODE).
    /// ▸ Closes any still-active “in/break” sessions for yesterday  
    /// ▸ Makes yesterday’s board read-only & un-pins it (kept in chat)  
    /// ▸ Creates & pins exactly **one** clean board for the new day  
    /// A per-chat semaphore prevents duplicate boards when timers overlap.
    private async Task ResetDayEndStatus()
    {
        try
        {
            _statusTimer?.Change(Timeout.Infinite, Timeout.Infinite); // pause 10-sec timer

            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();
            var chats = await repo.GetTrackedChatsAsync();

            var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz); // ≈ 00:00
            var yDate = iranNow.Date.AddDays(-1);
            var closeStamp = iranNow.AddSeconds(-1); // 23:59:59.999

            foreach (var chatId in chats)
            {
                var gate = _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                try
                {
                    /* add an “out” for EVERY user (even if already out ) */
                    var yEntries = await repo.GetUserEntriesAsync(chatId, yDate, closeStamp);

                    var lastPerUser = yEntries
                        .GroupBy(e => e.UserId)
                        .Select(g => g.OrderByDescending(e => e.Timestamp).First());

                    foreach (var last in lastPerUser)
                    {
                        await repo.AddEntryAsync(new TimeEntry
                        {
                            UserId = last.UserId,
                            Username = last.Username,
                            ChatId = chatId,
                            Timestamp = closeStamp,
                            Action = "out",
                            IsActive = false
                        });
                    }

                    // finalise yesterday & create today's clean board
                    await UpdateStatusMessage(chatId, repo);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Midnight reset failure");
        }
        finally
        {
            _statusTimer?.Change(TimeSpan.Zero, TimeSpan.FromSeconds(10)); // resume timer
        }
    }


    // ──────────────────────────────────────────────────────────────────────────
    // Telegram pipeline
    // ──────────────────────────────────────────────────────────────────────────
    private async Task HandleUpdateAsync(ITelegramBotClient bot,
        Update update,
        CancellationToken ct)
    {
        // presence-tracking works **only** in group/super-group chats
        if (!IsGroupChat(update))
            return;

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();

        try
        {
            switch (update.Type)
            {
                case UpdateType.CallbackQuery:
                    await HandleCallbackQuery(update.CallbackQuery!, repository);
                    break;

                case UpdateType.Message:
                    await HandleMessage(update.Message!, repository);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update-handling error");
        }
    }

    private static bool IsGroupChat(Update u)
    {
        return (u.Message?.Chat.Type, u.CallbackQuery?.Message?.Chat.Type) switch
        {
            (ChatType.Group or ChatType.Supergroup, _) => true,
            (_, ChatType.Group or ChatType.Supergroup) => true,
            _ => false
        };
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient bot,
        Exception ex,
        CancellationToken _)
    {
        var msg = ex switch
        {
            ApiRequestException apiEx => $"Telegram API error: [{apiEx.ErrorCode}] {apiEx.Message}",
            _ => ex.ToString()
        };
        _logger.LogError(msg);
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Messages
    // ──────────────────────────────────────────────────────────────────────────
    private async Task HandleMessage(Message message,
        ITimeEntryRepository repository)
    {
        if (message.Text?.StartsWith('/') == true)
            await HandleCommand(message, repository);
        else if (message.Chat.Type is ChatType.Group or ChatType.Supergroup)
            await UpdateStatusMessage(message.Chat.Id, repository);
    }

    private async Task HandleCommand(Message message,
        ITimeEntryRepository repository)
    {
        var parts = message.Text!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/start":
                // 1) fresh welcome message
                await SendWelcomeMessage(message.Chat.Id);

                // 2) move the status message to the bottom as well
                await RecreateStatusMessageAsync(message.Chat.Id, repository);
                break;

            case "/report":
                if (!await IsAdmin(message.Chat.Id, message.From!.Id))
                {
                    await SendTempMessage(message.Chat.Id, "❌ فقط ادمین‌ها می‌توانند گزارش دریافت کنند");
                    break;
                }

                // oldest entry in DB
                var oldest = await repository.GetOldestEntryDateAsync();
                if (oldest == null)
                {
                    await SendTempMessage(message.Chat.Id, "هنوز داده‌ای ثبت نشده است.");
                    break;
                }

                var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);
                var pc = new PersianCalendar();
                int firstYear = pc.GetYear(oldest.Value);
                int thisYear = pc.GetYear(iranNow);

                if (firstYear == thisYear) // only one Persian year
                {
                    var monthKb = await BuildMonthKeyboardAsync(message.Chat.Id, thisYear, repository);

                    if (!monthKb.InlineKeyboard.Any()) // no data at all for this year
                    {
                        await SendTempMessage(message.Chat.Id, "برای این سال داده‌ای وجود ندارد.");
                        break;
                    }

                    await _botClient!.SendMessage(
                        message.Chat.Id,
                        "لطفاً ماه مورد نظر را انتخاب کنید:",
                        replyMarkup: monthKb);
                }
                else // multiple years → show year picker
                {
                    await _botClient!.SendMessage(
                        message.Chat.Id,
                        "لطفاً سال مورد نظر را انتخاب کنید:",
                        replyMarkup: YearKeyboard(firstYear, thisYear));
                }

                break;

            default:
                await SendTempMessage(message.Chat.Id, "❌ دستور ناشناخته");
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Callback queries (inline-keyboard buttons)
    // ──────────────────────────────────────────────────────────────────────────
    /// <summary>
    ///     Routes every inline-keyboard callback coming from the bot.
    ///     Handles: report year/month picker, out-confirmation, and regular status buttons.
    /// </summary>
    private async Task HandleCallbackQuery(CallbackQuery query,
        ITimeEntryRepository repo)
    {
        if (string.IsNullOrEmpty(query.Data)) return;

        /******** 1️⃣  REPORT keyboard *******************************************/

        // Year selected  →  delete picker, show month keyboard
        if (query.Data.StartsWith(REP_YEAR))
        {
            var pYear = int.Parse(query.Data[REP_YEAR.Length..]);

            var monthKb = await BuildMonthKeyboardAsync(query.Message!.Chat.Id, pYear, repo);
            if (!monthKb.InlineKeyboard.Any())
            {
                await _botClient.AnswerCallbackQuery(query.Id,
                    "هیچ داده‌ای برای این سال وجود ندارد.");
                return;
            }

            await _botClient.DeleteMessage(query.Message.Chat.Id, query.Message.MessageId);

            await _botClient.SendMessage(
                query.Message.Chat.Id,
                $"✅ سال {pYear} انتخاب شد – ماه را انتخاب کنید:",
                replyMarkup: monthKb);

            return;
        }

        // Month selected  →  delete picker, build & send report
        if (query.Data.StartsWith(REP_MONTH))
        {
            var parts = query.Data[REP_MONTH.Length..].Split('_');
            var pYear = int.Parse(parts[0]);
            var pMonth = int.Parse(parts[1]);

            await _botClient.AnswerCallbackQuery(query.Id, "در حال ایجاد گزارش…");
            await _botClient.DeleteMessage(query.Message!.Chat.Id, query.Message.MessageId);

            await SendMonthlyReportAsync(query.Message.Chat.Id, pYear, pMonth, repo);
            return;
        }

        /******** 2️⃣  OUT-confirmation keyboard *********************************/

        if (query.Data == "confirm_out")
        {
            if (CheckOwner(query))
                await HandleConfirmedOut(query, repo);
            else
                await _botClient.AnswerCallbackQuery(query.Id, "❌ این تاییدیه مربوط به شما نیست");
            return;
        }

        if (query.Data == "cancel_out")
        {
            if (CheckOwner(query))
                await CancelOutConfirmation(query, repo);
            else
                await _botClient.AnswerCallbackQuery(query.Id, "❌ این تاییدیه مربوط به شما نیست");
            return;
        }

        /******** 3️⃣  Regular presence buttons **********************************/

        await HandleButtonClick(query, repo);

        /* helper */
        bool CheckOwner(CallbackQuery q)
        {
            return _pendingConfirmations.TryGetValue(q.Message!.MessageId, out var uid) && uid == q.From.Id;
        }
    }

    private async Task HandleConfirmedOut(CallbackQuery query,
        ITimeEntryRepository repository)
    {
        var user = query.From;
        var chat = query.Message!.Chat;
        var iranTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);

        // already out today?
        var todayEntries = await repository.GetUserEntriesAsync(chat.Id, iranTime.Date, iranTime);
        if (todayEntries.Any(e => e.UserId == user.Id && e.Action == "out"))
        {
            await _botClient!.AnswerCallbackQuery(query.Id,
                "⚠️ شما قبلاً وضعیت خروج را ثبت کرده‌اید.");
            return;
        }

        var entry = new TimeEntry
        {
            UserId = user.Id,
            Username = user.Username ?? $"{user.FirstName} {user.LastName}".Trim(),
            ChatId = chat.Id,
            Timestamp = iranTime,
            Action = "out",
            IsActive = false
        };

        await repository.AddEntryAsync(entry);
        await UpdateStatusMessage(chat.Id, repository);

        await _botClient!.AnswerCallbackQuery(query.Id, "✅ خارج شدن شما ثبت شد");
        _pendingConfirmations.Remove(query.Message.MessageId);
        if (_confirmTimers.TryGetValue(query.Message.MessageId, out var t)) t.Cancel();
        _confirmTimers.Remove(query.Message.MessageId);
    }

    private async Task CancelOutConfirmation(CallbackQuery query,
        ITimeEntryRepository repository)
    {
        await UpdateStatusMessage(query.Message!.Chat.Id, repository);
        await _botClient!.AnswerCallbackQuery(query.Id, "❌ خارج شدن لغو شد");
        _pendingConfirmations.Remove(query.Message.MessageId);
        if (_confirmTimers.TryGetValue(query.Message.MessageId, out var t)) t.Cancel();
        _confirmTimers.Remove(query.Message.MessageId);
    }

    private async Task HandleButtonClick(CallbackQuery query,
        ITimeEntryRepository repository)
    {
        try
        {
            await _botClient!.AnswerCallbackQuery(query.Id, "در حال پردازش…");

            var user = query.From;
            var chat = query.Message!.Chat;
            var iranTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);
            var today = await repository.GetUserEntriesAsync(chat.Id, iranTime.Date, iranTime);

            var userToday = today
                .Where(e => e.UserId == user.Id)
                .OrderByDescending(e => e.Timestamp)
                .ToList();
            var last = userToday.FirstOrDefault();

            /* 🚫 Already OUT today → no more actions allowed */
            if (last?.Action == "out")
            {
                await _botClient.AnswerCallbackQuery(query.Id,
                    "🚫 شما امروز خارج شده‌اید و نمی‌توانید وضعیت دیگری ثبت کنید.");
                return;
            }

            /* Same button pressed twice */
            if (last?.Action == query.Data)
            {
                await _botClient.AnswerCallbackQuery(query.Id,
                    $"⚠️ شما هم اکنون در وضعیت {TranslateAction(query.Data!)} هستید.");
                return;
            }

            if (query.Data == "out") // show confirmation dialog
            {
                await ShowOutConfirmation(query);
                return;
            }

            var entry = new TimeEntry
            {
                UserId = user.Id,
                Username = user.Username ?? $"{user.FirstName} {user.LastName}".Trim(),
                ChatId = chat.Id,
                Timestamp = iranTime,
                Action = query.Data!,
                IsActive = query.Data == "in"
            };

            await repository.AddEntryAsync(entry);
            await UpdateStatusMessage(chat.Id, repository);
            await _botClient.AnswerCallbackQuery(query.Id,
                $"وضعیت ثبت شد: {TranslateAction(query.Data!)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling button click");
            await _botClient!.AnswerCallbackQuery(query.Id, "خطا در پردازش درخواست");
        }
    }


    /// Sends a separate 10-second confirmation message that updates once per
    /// second.  Does **not** interfere with the pinned status board.
    /// If the user neither confirms nor cancels, the confirmation message
    /// auto-deletes after the countdown.
    private async Task ShowOutConfirmation(CallbackQuery query)
    {
        const int WINDOW = 10; // seconds
        var chatId = query.Message!.Chat.Id;
        var userId = query.From.Id;

        // keyboard for the confirmation message
        var kb = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ تایید خروج", "confirm_out"),
                InlineKeyboardButton.WithCallbackData("❌ انصراف", "cancel_out")
            }
        });

        // helper to produce countdown text
        string Body(int s) =>
            $"⚠️ <b>تایید خروج برای {query.From.FirstName}</b>\n\n" +
            $"زمان باقی‌مانده: <b>{s}</b> ثانیه";

        // send a *new* message (do NOT erase the main board)
        var msg = await _botClient!.SendMessage(chatId, Body(WINDOW),
            ParseMode.Html, replyMarkup: kb);

        int msgId = msg.MessageId;
        _pendingConfirmations[msgId] = userId;

        var cts = new CancellationTokenSource();
        _confirmTimers[msgId] = cts;

        // background countdown – 10 edits, one per second
        _ = Task.Run(async () =>
        {
            try
            {
                for (int s = WINDOW - 1; s >= 0; s--)
                {
                    await Task.Delay(1000, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    try
                    {
                        await _botClient.EditMessageText(chatId, msgId, Body(s),
                            ParseMode.Html, replyMarkup: kb);
                    }
                    catch (Exception)
                    {
                        /* message might be gone */
                    }
                }

                // time expired → auto-cancel
                if (!cts.IsCancellationRequested)
                {
                    _pendingConfirmations.Remove(msgId);
                    _confirmTimers.Remove(msgId);

                    try
                    {
                        await _botClient.DeleteMessage(chatId, msgId);
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
                /* cancelled on confirm/cancel */
            }
        }, cts.Token);
    }


    private static InlineKeyboardMarkup YearKeyboard(int fromYear, int toYear)
    {
        // Persian years descending
        var buttons = Enumerable.Range(fromYear, toYear - fromYear + 1)
            .Reverse()
            .Select(y => InlineKeyboardButton.WithCallbackData($"سال {y}", REP_YEAR + y))
            .Chunk(3) // 3 buttons per row
            .Select(r => r.ToArray())
            .ToArray();
        return new InlineKeyboardMarkup(buttons);
    }

    private static InlineKeyboardMarkup MonthKeyboard(int persianYear)
    {
        // Persian month names (fa-IR); the last element of MonthNames is empty, so skip it.
        var monthNames = new CultureInfo("fa-IR")
            .DateTimeFormat
            .MonthNames
            .Take(12) // keep Farvardin..Esfand
            .ToArray();

        var buttons = Enumerable.Range(1, 12)
            .Select(m => InlineKeyboardButton.WithCallbackData(
                monthNames[m - 1], // label
                $"{REP_MONTH}{persianYear}_{m:00}")) // callback data
            .Chunk(3) // 3 buttons per row
            .Select(row => row.ToArray())
            .ToArray();

        return new InlineKeyboardMarkup(buttons);
    }

    /// Builds a keyboard with only the months that actually contain data.
    /// Builds a Persian-month keyboard that **only shows months which contain data**.
    private async Task<InlineKeyboardMarkup> BuildMonthKeyboardAsync(long chatId,
        int pYear,
        ITimeEntryRepository repo)
    {
        var pc = new PersianCalendar();
        var gFrom = pc.ToDateTime(pYear, 1, 1, 0, 0, 0, 0); // 1 Farvardin
        var gTo = pc.ToDateTime(pYear + 1, 1, 1, 0, 0, 0, 0).AddTicks(-1);

        var entries = await repo.GetUserEntriesAsync(chatId, gFrom, gTo);

        var monthsWithData = entries
            .Select(e => pc.GetMonth(e.Timestamp))
            .Distinct()
            .ToHashSet();

        if (monthsWithData.Count == 0) // no entries for this year
            return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        // Persian month names via fa-IR culture
        var monthNames = new CultureInfo("fa-IR")
            .DateTimeFormat
            .MonthNames
            .Take(12)
            .ToArray();

        var buttons = Enumerable.Range(1, 12)
            .Where(m => monthsWithData.Contains(m))
            .Select(m => InlineKeyboardButton.WithCallbackData(
                monthNames[m - 1], $"{REP_MONTH}{pYear}_{m:00}"))
            .Chunk(3)
            .Select(row => row.ToArray())
            .ToArray();

        return new InlineKeyboardMarkup(buttons);
    }

    /// On startup: guarantees each chat already has **today’s** board.
    /// If the stored board belongs to a previous day, it is un-pinned,
    /// its buttons removed, the DB record cleared, and a new board is created.
    private async Task EnsureDailyStatusBoardAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);
        var chatIds = await repo.GetTrackedChatsAsync();

        foreach (var chatId in chatIds)
        {
            var rec = await repo.GetChatMessageAsync(chatId);

            if (rec != null && rec.LastUpdated.Date < iranNow.Date)
            {
                try
                {
                    await _botClient!.EditMessageReplyMarkup(chatId, rec.MessageId, null);
                }
                catch
                {
                }

                try
                {
                    await _botClient.UnpinChatMessage(chatId, rec.MessageId);
                }
                catch
                {
                }

                await repo.DeleteChatMessageAsync(chatId); // drop pointer, keep message
                rec = null;
            }

            if (rec == null) // create board for today
                await UpdateStatusMessage(chatId, repo);
        }
    }


// ──────────────────────────────────────────────────────────────────────────
// Status board (create / refresh / replace)
// ──────────────────────────────────────────────────────────────────────────
// ───────────────────────────────────────── Status Board ─────────────────────────────────────────
    private async Task UpdateStatusMessage(long chatId, ITimeEntryRepository repo)
    {
        var gate = _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(); // ← acquire
        try
        {
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz).Date;
            var boardRec = await repo.GetChatMessageAsync(chatId); // pointer for *today* if any
            var text = await GenerateStatusText(chatId, repo);
            var keyboard = CreateStatusKeyboard();

            /* —— If the stored board is for an earlier day ——————————————— */
            if (boardRec != null && boardRec.LastUpdated.Date < today)
            {
                // make yesterday’s board read-only & leave it in chat
                try
                {
                    await _botClient!.EditMessageReplyMarkup(chatId, boardRec.MessageId, replyMarkup: null);
                }
                catch
                {
                }

                try
                {
                    await _botClient.UnpinChatMessage(chatId, boardRec.MessageId);
                }
                catch
                {
                }

                await repo.DeleteChatMessageAsync(chatId); // forget pointer; DON'T delete the message
                boardRec = null; // force fresh board below
            }

            /* —— Either update today’s board or create a fresh one ————————— */
            if (boardRec == null)
            {
                var msg = await _botClient!.SendMessage(chatId, text, ParseMode.Html, replyMarkup: keyboard);

                await repo.SaveChatMessageAsync(new ChatMessage
                {
                    ChatId = chatId,
                    MessageId = msg.MessageId,
                    LastUpdated = today,
                    MessageType = "status"
                });

                boardRec = new ChatMessage { MessageId = msg.MessageId };
            }
            else
            {
                await _botClient!.EditMessageText(chatId, boardRec.MessageId, text, ParseMode.Html,
                    replyMarkup: keyboard);
                boardRec.LastUpdated = today;
                await repo.SaveChatMessageAsync(boardRec);
            }

            try
            {
                await _botClient.PinChatMessage(chatId, boardRec.MessageId);
            }
            catch
            {
            }
        }
        finally
        {
            gate.Release();
        } // ← release
    }


    private async Task<string> GenerateStatusText(long chatId,
        ITimeEntryRepository repository)
    {
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);
        var todayList = await repository.GetUserEntriesAsync(chatId, iranNow.Date, iranNow);

        var users = todayList
            .GroupBy(e => e.UserId)
            .Select(g => new
            {
                Info = g.First(),
                Latest = g.OrderByDescending(e => e.Timestamp).First(),
                AllEntries = g.OrderBy(e => e.Timestamp).ToList(),
                WorkTime = CalcWorkTime(g, iranNow)
            })
            .OrderBy(u => u.Info.Username);

        var sb = new StringBuilder();
        sb.AppendLine("🕒 <b>وضعیت حضور تیم</b>");
        sb.AppendLine($"<i>{ConvertToPersianDateTime(iranNow)}</i>");
        sb.AppendLine("<code>────────────────────────────</code>");

        foreach (var u in users)
        {
            var firstIn = u.AllEntries.FirstOrDefault(e => e.Action == "in");
            var lastOut = u.AllEntries.LastOrDefault(e => e.Action == "out");
            var lastAct = u.AllEntries.Last();

            // clickable profile link
            var link = $"<a href=\"tg://user?id={u.Info.UserId}\">{u.Info.Username}</a>";
            sb.AppendLine($"{GetActionEmoji(u.Latest.Action)} {link}");
            sb.AppendLine($"⏳ کارکرد امروز: <code>{u.WorkTime:hh\\:mm}</code>");

            if (firstIn != null)
                sb.AppendLine($"🟢 شروع کار: <code>{firstIn.Timestamp:HH:mm}</code>");

            if (lastAct != null)
            {
                var s = u.Latest.Action == "out"
                    ? "🔴 خارج شده"
                    : $"🔄 آخرین فعالیت: <code>{lastAct.Timestamp:HH:mm}</code> ({TranslateAction(lastAct.Action)})";
                sb.AppendLine(s);
            }

            if (lastOut != null)
            {
                sb.AppendLine($"🔴 پایان کار: <code>{lastOut.Timestamp:HH:mm}</code>");
                sb.AppendLine("🚫 امکان حضور مجدد امروز وجود ندارد");
            }

            sb.AppendLine("<code>────────────────────────────</code>");
        }

        return sb.ToString();
    }


    private InlineKeyboardMarkup CreateStatusKeyboard()
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🟢In", "in"),
                InlineKeyboardButton.WithCallbackData("🟡Break", "break"),
                InlineKeyboardButton.WithCallbackData("🔴Out", "out")
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reports
    // ──────────────────────────────────────────────────────────────────────────
    private async Task HandleReportCommand(long chatId,
        string period,
        ITimeEntryRepository repository)
    {
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);

        (DateTime from, DateTime to) span = period.ToLowerInvariant() switch
        {
            "year" => (new DateTime(iranNow.Year, 1, 1), iranNow),
            "month" => (new DateTime(iranNow.Year, iranNow.Month, 1), iranNow),
            _ => (iranNow.Date, iranNow)
        };

        var entries = await repository.GetUserEntriesAsync(chatId, span.from, span.to);

        var file = await GenerateReportExcel(entries, span.from, span.to);

        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        await _botClient!.SendDocument(chatId,
            new InputFileStream(fs, Path.GetFileName(file)),
            "گزارش زمان کاری");
    }

    public async Task<string> GenerateReportExcel(
        List<TimeEntry> entries,
        DateTime from,
        DateTime to)
    {
        var pc = new PersianCalendar(); // Persian calendar helper

        // ──────────  create workbook  ──────────
        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Report");

        worksheet.Cell(1, 1).Value = "نام کاربر";
        worksheet.Cell(1, 2).Value = "تعداد روزهای کاری";
        worksheet.Cell(1, 3).Value = "مجموع ساعت‌های کاری";

        // keep the original Gregorian range for filtering ➜ no out-of-range risk
        var userGroups = entries
            .Where(e => e.Timestamp.Date >= from.Date &&
                        e.Timestamp.Date <= to.Date &&
                        e.Action == "in")
            .GroupBy(e => e.UserId);

        var row = 2;
        foreach (var g in userGroups)
        {
            var user = g.First();
            var total = CalcWorkTime(g, to); // uses your fixed calculator

            worksheet.Cell(row, 1).Value = user.Username ?? $"{user.FirstName} {user.LastName}".Trim();
            worksheet.Cell(row, 2).Value = g.Select(e => e.Timestamp.Date).Distinct().Count();
            worksheet.Cell(row, 3).Value = total.ToString(@"hh\:mm");
            row++;
        }

        worksheet.Columns().AdjustToContents();

        // file-name in Persian calendar – **string composition only**
        var fileName =
            $"Salmej_Export_{pc.GetYear(from)}{pc.GetMonth(from):00}{pc.GetDayOfMonth(from):00}.xlsx";

        var dir = Path.Combine("Reports");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, fileName);
        workbook.SaveAs(path);
        return path;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Utilities
    // ──────────────────────────────────────────────────────────────────────────
    private async Task SendTempMessage(long chatId, string text)
    {
        await _botClient!.SendMessage(chatId, text);
    }

    private async Task SendWelcomeMessage(long chatId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITimeEntryRepository>();

        // 1) Try to load the previous welcome-message from DB
        var previous = repo?.GetWelcomeMessageAsync(chatId) ?? null;
        var prevMsg = previous != null ? await previous : null;

        // 2) Delete it in Telegram & DB
        if (prevMsg != null)
        {
            try
            {
                await _botClient!.DeleteMessage(chatId, prevMsg.MessageId);
            }
            catch
            {
                /* ignored – message might have been removed manually */
            }

            if (repo is ITimeEntryRepository concrete)
                await concrete.SaveWelcomeMessageAsync(new ChatMessage
                {
                    ChatId = chatId,
                    MessageId = 0, // clear
                    LastUpdated = DateTime.UtcNow,
                    MessageType = "welcome"
                });
        }

        // 3) Send fresh welcome-message (appears at bottom)
        var sent = await _botClient!.SendMessage(
            chatId,
            "🕒 <b>ربات ثبت زمان کار</b>\n\n" +
            "از دکمه‌های زیر برای ثبت وضعیت کاری خود استفاده کنید!\n" +
            "ادمین‌ها می‌توانند از /report برای دریافت گزارش استفاده کنند",
            ParseMode.Html,
            replyMarkup: CreateStatusKeyboard());

        // 4) Persist its MessageId
        if (repo is ITimeEntryRepository concreteRepo)
            await concreteRepo.SaveWelcomeMessageAsync(new ChatMessage
            {
                ChatId = chatId,
                MessageId = sent.MessageId,
                LastUpdated = DateTime.UtcNow,
                MessageType = "welcome"
            });
    }

    private async Task SendMonthlyReportAsync(long chatId, int pYear, int pMonth,
        ITimeEntryRepository repo)
    {
        var pc = new PersianCalendar();

        // convert Persian year/month -> Gregorian range
        var from = pc.ToDateTime(pYear, pMonth, 1, 0, 0, 0, 0);
        var to = from.AddMonths(1).AddTicks(-1); // end of the month

        var entries = await repo.GetUserEntriesAsync(chatId, from, to);

        if (!entries.Any())
        {
            await SendTempMessage(chatId, "برای این بازه زمانی داده‌ای وجود ندارد.");
            return;
        }

        var file = await BuildMonthlyExcel(entries, pYear, pMonth);

        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
        await _botClient.SendDocument(chatId,
            new InputFileStream(fs, Path.GetFileName(file)),
            $"گزارش ماه {pMonth} سال {pYear}");
    }

    /// Creates a month-wide Excel report with accurate daily & monthly totals
    /// and Persian weekday names.
    private async Task<string> BuildMonthlyExcel(List<TimeEntry> entries,
        int pYear, int pMonth)
    {
        var pc = new PersianCalendar();
        var gFrom = pc.ToDateTime(pYear, pMonth, 1, 0, 0, 0, 0);
        var gTo = gFrom.AddMonths(1).AddTicks(-1);
        var iranNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _iranTz);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Month");

        /* ───── headers ───── */
        ws.Cell(1, 1).Value = "کاربر";
        ws.Cell(1, 2).Value = "روز هفته";
        ws.Cell(1, 3).Value = "تاریخ";
        ws.Cell(1, 4).Value = "شروع";
        ws.Cell(1, 5).Value = "پایان";
        ws.Cell(1, 6).Value = "ساعت کار";

        var row = 2;

        // group by (User , Gregorian Day)
        var dayGroups = entries
            .GroupBy(e => new { e.UserId, e.Timestamp.Date })
            .OrderBy(g => g.Key.Date)
            .ThenBy(g => g.First().Username);

        foreach (var g in dayGroups)
        {
            var dayEntries = g.OrderBy(e => e.Timestamp).ToList();

            // earliest IN   / latest OUT (if none, last action)
            var firstIn = dayEntries.FirstOrDefault(e => e.Action == "in")?.Timestamp;
            var lastOut = dayEntries.LastOrDefault(e => e.Action == "out")?.Timestamp;

            // if user is still "in" at end-of-day, measure until day-end (or now if today)
            DateTime periodEnd;
            if (lastOut != null)
            {
                periodEnd = lastOut.Value;
            }
            else
            {
                var dayEnd = g.Key.Date.AddDays(1).AddSeconds(-1);
                periodEnd = g.Key.Date == iranNow.Date ? iranNow : dayEnd;
            }

            var work = CalcWorkTime(dayEntries, periodEnd);

            /* write row */
            var user = dayEntries.First();
            var d = g.Key.Date;

            // Persian weekday name
            var faDayName = new CultureInfo("fa-IR").DateTimeFormat.GetDayName(d.DayOfWeek);

            ws.Cell(row, 1).Value = user.Username;
            ws.Cell(row, 2).Value = faDayName;
            ws.Cell(row, 3).Value = $"{pc.GetYear(d)}/{pc.GetMonth(d):00}/{pc.GetDayOfMonth(d):00}";
            ws.Cell(row, 4).Value = firstIn?.ToString("HH:mm") ?? "-";
            ws.Cell(row, 5).Value = periodEnd.ToString("HH:mm");
            ws.Cell(row, 6).Value = work.ToString(@"hh\:mm");

            row++;
        }

        /* ───── summary ───── */
        row += 2;
        ws.Cell(row, 1).Value = "خلاصه ماه";
        row++;

        var userGroups = entries.GroupBy(e => e.UserId);
        foreach (var g in userGroups)
        {
            var periodEnd = gTo > iranNow ? iranNow : gTo;
            var totalWork = CalcWorkTime(g, periodEnd);
            var workDays = g.Select(e => e.Timestamp.Date).Distinct().Count();
            var user = g.First();

            ws.Cell(row, 1).Value = user.Username;
            ws.Cell(row, 2).Value = $"{workDays} روز";
            ws.Cell(row, 3).Value = totalWork.ToString(@"hh\:mm");
            row++;
        }

        ws.Columns().AdjustToContents();

        /* save */
        var dir = "Reports";
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"Monthly_{pYear}_{pMonth:00}.xlsx");
        wb.SaveAs(file);
        return file;
    }


    /// <summary>
    ///     Deletes the current status-message for the chat (if any) and creates a fresh one
    ///     so it appears at the bottom of the group.
    /// </summary>
    private async Task RecreateStatusMessageAsync(long chatId, ITimeEntryRepository repository)
    {
        // load the current (pinned) status record from DB
        var old = await repository.GetChatMessageAsync(chatId);
        if (old != null)
        {
            // try to un-pin and delete it in Telegram
            try
            {
                await _botClient!.UnpinChatMessage(chatId, old.MessageId);
            }
            catch
            {
            }

            try
            {
                await _botClient!.DeleteMessage(chatId, old.MessageId);
            }
            catch
            {
            }

            // remove the record from DB
            await repository.DeleteChatMessageAsync(chatId);
        }

        // UpdateStatusMessage will now create & pin a brand-new one at the bottom
        await UpdateStatusMessage(chatId, repository);
    }


    private async Task<bool> IsAdmin(long chatId, long userId)
    {
        try
        {
            var cm = await _botClient!.GetChatMember(chatId, userId);
            return cm.Status is ChatMemberStatus.Administrator or ChatMemberStatus.Creator;
        }
        catch
        {
            return false;
        }
    }

    /* ──────────  FIXED: counts ongoing “in” session & no double-count on repeated breaks */
    private static TimeSpan CalcWorkTime(IEnumerable<TimeEntry> entries, DateTime now)
    {
        var ordered = entries.OrderBy(e => e.Timestamp);
        var total = TimeSpan.Zero;
        DateTime? lastIn = null;

        foreach (var e in ordered)
            switch (e.Action)
            {
                case "in":
                    lastIn = e.Timestamp;
                    break;

                case "break":
                case "out":
                    if (lastIn.HasValue)
                    {
                        total += e.Timestamp - lastIn.Value;
                        lastIn = null; // pause the clock
                    }

                    break;
            }

        if (lastIn.HasValue) // still “in” → add time up to *now*
            total += now - lastIn.Value;

        return total;
    }

    private static string GetActionEmoji(string action)
    {
        return action switch
        {
            "in" => "🟢",
            "break" => "🟡",
            "out" => "🔴",
            _ => "⚪"
        };
    }

    private static string TranslateAction(string action)
    {
        return action switch
        {
            "in" => "حضور",
            "break" => "استراحت",
            "out" => "خروج",
            _ => action
        };
    }

    private static string ConvertToPersianDate(DateTime dt)
    {
        var pc = new PersianCalendar();
        return $"{pc.GetYear(dt)}/{pc.GetMonth(dt):00}/{pc.GetDayOfMonth(dt):00}";
    }

    private static string ConvertToPersianDateTime(DateTime dt)
    {
        return $"{ConvertToPersianDate(dt)} {dt:HH:mm}";
    }
}